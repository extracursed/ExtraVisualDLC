// Vercel Function: админка пользователей.
// GET  /api/admin-users?q=...        → список (Админ, Модератор)
// POST /api/admin-users               → действие (права по ролям)
//   { action: "ban" | "unban" }                — только Админ
//   { action: "freeze" | "unfreeze" }           — Админ, Модератор
//   { action: "reset_hwid" }                     — только Админ (сброс привязки ПК)
//   { action: "delete_user" }                    — только Админ (удаление аккаунта)
//   { action: "set_role", role }                — только Админ
//   + { user_id }
//
// Авторизация: заголовок Authorization: Bearer <access_token>.
// Env: SUPABASE_URL, SUPABASE_SERVICE_ROLE_KEY (уже есть).

const { createClient } = require("@supabase/supabase-js");

const ROLES = ["Пользователь", "Модератор", "Админ", "Разработчик", "Кирюха"];
// Полные права: Админ, Разработчик, Кирюха.
// Топ-роли (Разработчик, Кирюха) выдавать/снимать может только Разработчик.
const FULL = ["Админ", "Разработчик", "Кирюха"];
const TOP = ["Разработчик", "Кирюха"];

function admin() {
  return createClient(process.env.SUPABASE_URL, process.env.SUPABASE_SERVICE_ROLE_KEY);
}

async function callerRole(token) {
  if (!token) return null;
  const svc = admin();
  const { data, error } = await svc.auth.getUser(token);
  if (error || !data.user) return null;
  const { data: prof } = await svc
    .from("profiles")
    .select("role")
    .eq("user_id", data.user.id)
    .single();
  return { id: data.user.id, role: (prof && prof.role) || "Пользователь" };
}

const can = (role, action) => {
  if (FULL.includes(role)) return true;
  if (role === "Модератор") return ["view", "freeze", "unfreeze"].includes(action);
  return false;
};

async function listUsers(q) {
  const svc = admin();
  const users = [];
  let page = 1;
  for (let i = 0; i < 10; i++) {
    const { data, error } = await svc.auth.admin.listUsers({ page, perPage: 100 });
    if (error) throw error;
    users.push(...data.users);
    if (data.users.length < 100) break;
    page++;
  }
  const ids = users.map((u) => u.id);
  let profMap = {};
  if (ids.length) {
    const { data: profs } = await svc.from("profiles").select("user_id, id, username, role, frozen, telegram_chat_id, hwid").in("user_id", ids);
    (profs || []).forEach((p) => { profMap[p.user_id] = p; });
  }
  const query = String(q || "").toLowerCase();
  return users
    .map((u) => {
      const p = profMap[u.id] || {};
      return {
        user_id: u.id,
        uid: p.id || null,
        username: p.username || (u.user_metadata && u.user_metadata.username) || null,
        email: u.email,
        role: p.role || "Пользователь",
        frozen: !!p.frozen,
        banned: !!u.banned_until && new Date(u.banned_until).getTime() > Date.now(),
        tg: p.telegram_chat_id != null,
        hwid: p.hwid || null,
        created_at: u.created_at,
      };
    })
    .filter((u) =>
      !query ||
      (u.email || "").toLowerCase().includes(query) ||
      (u.username || "").toLowerCase().includes(query) ||
      String(u.uid || "").includes(query)
    )
    .sort((a, b) => (a.uid || 0) - (b.uid || 0));
}

module.exports = async (req, res) => {
  if (!process.env.SUPABASE_URL || !process.env.SUPABASE_SERVICE_ROLE_KEY)
    return res.status(500).json({ error: "Сервер не настроен" });

  const token = (req.headers.authorization || "").replace(/^Bearer\s+/i, "");
  const caller = await callerRole(token);
  if (!caller) return res.status(401).json({ error: "Войди в аккаунт" });

  try {
    if (req.method === "GET") {
      if (!can(caller.role, "view")) return res.status(403).json({ error: "Нет доступа" });
      const users = await listUsers(req.query && req.query.q);
      return res.status(200).json({ ok: true, users, myRole: caller.role });
    }

    if (req.method === "POST") {
      const { action, user_id, role } = req.body || {};
      if (!user_id) return res.status(400).json({ error: "Нет user_id" });
      if (user_id === caller.id) return res.status(400).json({ error: "Нельзя менять себя" });
      if (!can(caller.role, action)) return res.status(403).json({ error: "Нет прав на это действие" });

      const svc = admin();
      if (action === "ban") {
        const { error } = await svc.auth.admin.updateUserById(user_id, { ban_duration: "876000h" });
        if (error) throw error;
      } else if (action === "unban") {
        const { error } = await svc.auth.admin.updateUserById(user_id, { ban_duration: "none" });
        if (error) throw error;
      } else if (action === "freeze" || action === "unfreeze") {
        const { error } = await svc.from("profiles").update({ frozen: action === "freeze" }).eq("user_id", user_id);
        if (error) throw error;
      } else if (action === "reset_hwid") {
        const { error } = await svc.from("profiles").update({ hwid: null }).eq("user_id", user_id);
        if (error) throw error;
      } else if (action === "delete_user") {
        const { data: target } = await svc.from("profiles").select("role, username").eq("user_id", user_id).single();
        if (target && TOP.includes(target.role) && caller.role !== "Разработчик")
          return res.status(403).json({ error: "Удалять топ-роли может только Разработчик" });
        const { error } = await svc.auth.admin.deleteUser(user_id);
        if (error) throw error;
        await svc.from("profiles").delete().eq("user_id", user_id).then(() => {});
      } else if (action === "set_role") {
        if (!ROLES.includes(role)) return res.status(400).json({ error: "Нет такой роли" });
        // Топ-роли выдаёт только Разработчик
        if (TOP.includes(role) && caller.role !== "Разработчик")
          return res.status(403).json({ error: "Роли Разработчик и Кирюха выдаёт только Разработчик" });
        // Снимать топ-роль тоже может только Разработчик
        const { data: target } = await svc.from("profiles").select("role").eq("user_id", user_id).single();
        if (target && TOP.includes(target.role) && caller.role !== "Разработчик")
          return res.status(403).json({ error: "Трогать топ-роли может только Разработчик" });
        const { error } = await svc.from("profiles").update({ role }).eq("user_id", user_id);
        if (error) throw error;
      } else {
        return res.status(400).json({ error: "Неизвестное действие" });
      }
      return res.status(200).json({ ok: true });
    }

    return res.status(405).json({ error: "Method Not Allowed" });
  } catch (e) {
    console.error("[admin-users]", e);
    return res.status(500).json({ error: "Внутренняя ошибка" });
  }
};
