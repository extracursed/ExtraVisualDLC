// Vercel Function: вебхук Telegram-бота @ExtraVisualDLCBot.
// POST https://www.extravisualdlc.online/api/telegram-webhook
//
// Логика:
//   /start или /reset → просим прислать email
//   email → если аккаунт есть, генерируем 6-значный код (10 минут) и шлём в чат
//   иначе → одинаковый нейтральный ответ (защита от перебора email)
//
// Env (Vercel → Settings → Environment Variables, Production):
//   TELEGRAM_BOT_TOKEN      — токен от @BotFather (секрет!)
//   SUPABASE_URL            — https://vzfjwchkdaafnsoxzddt.supabase.co
//   SUPABASE_SERVICE_ROLE_KEY — sb_secret_... (секрет! только сервер)
//   TG_WEBHOOK_SECRET       — любое случайное слово (защита вебхука, опционально)

const crypto = require("crypto");
const { createClient } = require("@supabase/supabase-js");

const CODE_TTL_MIN = 10;

const esc = (s) =>
  String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
const isEmail = (s) => /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(s);

async function tgSend(token, chatId, text) {
  await fetch(`https://api.telegram.org/bot${token}/sendMessage`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ chat_id: chatId, text, parse_mode: "HTML" }),
  }).catch(() => {});
}

async function findUserId(admin, email) {
  const target = email.toLowerCase();
  let page = 1;
  for (let i = 0; i < 10; i++) {
    const { data, error } = await admin.auth.admin.listUsers({ page, perPage: 100 });
    if (error) throw error;
    const found = data.users.find((u) => (u.email || "").toLowerCase() === target);
    if (found) return found.id;
    if (data.users.length < 100) break;
    page++;
  }
  return null;
}

module.exports = async (req, res) => {
  if (req.method !== "POST") return res.status(405).send("Method Not Allowed");

  const BOT_TOKEN = process.env.TELEGRAM_BOT_TOKEN;
  const SUPABASE_URL = process.env.SUPABASE_URL;
  const SERVICE_KEY = process.env.SUPABASE_SERVICE_ROLE_KEY;
  const WEBHOOK_SECRET = process.env.TG_WEBHOOK_SECRET || "";
  if (!BOT_TOKEN || !SUPABASE_URL || !SERVICE_KEY)
    return res.status(500).send("Not configured");

  if (
    WEBHOOK_SECRET &&
    req.headers["x-telegram-bot-api-secret-token"] !== WEBHOOK_SECRET
  )
    return res.status(401).send("Unauthorized");

  try {
    const msg = req.body && req.body.message;
    const chatId = msg && msg.chat && msg.chat.id;
    const text = ((msg && msg.text) || "").trim();
    if (!chatId || !text) return res.status(200).send("OK");

    if (text === "/start" || text.startsWith("/start ") || text === "/reset") {
      // Привязка Telegram к аккаунту: кнопка на сайте ведёт на t.me/Bot?start=link_<user_id>
      const m = text.match(/^\/start\s+link_([0-9a-f-]{36})$/i);
      if (m) {
        const admin = createClient(SUPABASE_URL, SERVICE_KEY);
        const { data: u, error: uErr } = await admin.auth.admin.getUserById(m[1]);
        if (uErr || !u.user) {
          await tgSend(BOT_TOKEN, chatId, "Не нашёл такой аккаунт. Открой привязку заново из профиля на сайте.");
          return res.status(200).send("OK");
        }
        const { error: upErr } = await admin.from("profiles").update({ telegram_chat_id: chatId }).eq("user_id", m[1]);
        if (upErr) throw upErr;
        await tgSend(
          BOT_TOKEN,
          chatId,
          `Готово! Telegram привязан к аккаунту <b>${esc(u.user.email || "")}</b> ✅\n\nТеперь в профиле горит «Привязан», а коды сброса пароля будут приходить сюда.`
        );
        return res.status(200).send("OK");
      }
      await tgSend(
        BOT_TOKEN,
        chatId,
        "Привет! Это бот <b>ExtraVisualDLC</b>.\n\n<b>Привязка:</b> нажми «Привязать Telegram» в профиле на сайте — бот всё сделает сам.\n\n<b>Сброс пароля:</b> отправь сюда <b>email</b>, указанный при регистрации."
      );
      return res.status(200).send("OK");
    }

    if (!isEmail(text)) {
      await tgSend(
        BOT_TOKEN,
        chatId,
        "Похоже, это не email. Отправь email от аккаунта или нажми /reset, чтобы начать заново."
      );
      return res.status(200).send("OK");
    }

    const email = text.toLowerCase();
    const admin = createClient(SUPABASE_URL, SERVICE_KEY);
    const userId = await findUserId(admin, email);

    if (userId) {
      const code = crypto.randomInt(100000, 1000000).toString();
      const codeHash = crypto
        .createHash("sha256")
        .update(email + ":" + code)
        .digest("hex");
      const expiresAt = new Date(Date.now() + CODE_TTL_MIN * 60 * 1000).toISOString();

      await admin.from("password_reset_codes").delete().eq("email", email);
      const { error } = await admin.from("password_reset_codes").insert({
        email,
        code_hash: codeHash,
        expires_at: expiresAt,
        consumed: false,
        attempts: 0,
      });
      if (error) throw error;

      await tgSend(
        BOT_TOKEN,
        chatId,
        `Твой код для сброса пароля: <code>${code}</code>\n\nДействует ${CODE_TTL_MIN} минут. Введи его на сайте в окне «Восстановление пароля».`
      );

      // Привязываем Telegram к профилю (для галочки «Привязан» в профиле)
      try {
        await admin.from("profiles").update({ telegram_chat_id: chatId }).eq("user_id", userId);
      } catch (_) {}
    }

    // Нейтральный ответ в любом случае — не палим, есть ли такой email
    await tgSend(
      BOT_TOKEN,
      chatId,
      `Если аккаунт с <b>${esc(email)}</b> существует — код уже отправлен выше ⬆️\n\nВведи его на сайте вместе с новым паролем.`
    );
    return res.status(200).send("OK");
  } catch (e) {
    console.error("[tg-webhook]", e);
    return res.status(200).send("OK"); // Telegram не должен ретраить
  }
};
