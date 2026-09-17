// Vercel Function: смена пароля по коду из Telegram.
// POST https://www.extravisualdlc.online/api/telegram-reset
// Body: { email, code, newPassword } → { ok: true } или { error: "..." }
//
// Env (те же, что и у вебхука):
//   SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY

const crypto = require("crypto");
const { createClient } = require("@supabase/supabase-js");

const MAX_ATTEMPTS = 5;
const isEmail = (s) => /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/.test(String(s || ""));

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
  if (req.method !== "POST") return res.status(405).json({ error: "Method Not Allowed" });

  const SUPABASE_URL = process.env.SUPABASE_URL;
  const SERVICE_KEY = process.env.SUPABASE_SERVICE_ROLE_KEY;
  if (!SUPABASE_URL || !SERVICE_KEY)
    return res.status(500).json({ error: "Сервер не настроен" });

  try {
    const email = String((req.body && req.body.email) || "").trim().toLowerCase();
    const code = String((req.body && req.body.code) || "").trim();
    const newPassword = String((req.body && req.body.newPassword) || "");

    if (!isEmail(email)) return res.status(400).json({ error: "Некорректный email" });
    if (!/^\d{6}$/.test(code)) return res.status(400).json({ error: "Код — 6 цифр из Telegram" });
    if (newPassword.length < 6)
      return res.status(400).json({ error: "Новый пароль — минимум 6 символов" });

    const admin = createClient(SUPABASE_URL, SERVICE_KEY);

    const { data: rows, error: selErr } = await admin
      .from("password_reset_codes")
      .select("id, code_hash, expires_at, consumed, attempts")
      .eq("email", email)
      .eq("consumed", false)
      .order("created_at", { ascending: false })
      .limit(1);
    if (selErr) throw selErr;

    const row = rows && rows[0];
    const wrong = async () => {
      if (row) {
        const attempts = (row.attempts || 0) + 1;
        await admin
          .from("password_reset_codes")
          .update({
            attempts,
            consumed: attempts >= MAX_ATTEMPTS,
          })
          .eq("id", row.id);
      }
      return res.status(400).json({ error: "Неверный или просроченный код" });
    };

    if (!row) return await wrong();
    if (new Date(row.expires_at).getTime() < Date.now()) {
      await admin.from("password_reset_codes").update({ consumed: true }).eq("id", row.id);
      return await wrong();
    }

    const want = crypto.createHash("sha256").update(email + ":" + code).digest();
    const got = Buffer.from(row.code_hash, "hex");
    if (want.length !== got.length || !crypto.timingSafeEqual(want, got)) {
      return await wrong();
    }

    const userId = await findUserId(admin, email);
    if (!userId) return await wrong();

    const { error: updErr } = await admin.auth.admin.updateUserById(userId, {
      password: newPassword,
    });
    if (updErr) throw updErr;

    await admin.from("password_reset_codes").delete().eq("email", email);
    return res.status(200).json({ ok: true });
  } catch (e) {
    console.error("[tg-reset]", e);
    return res.status(500).json({ error: "Внутренняя ошибка, попробуй позже" });
  }
};
