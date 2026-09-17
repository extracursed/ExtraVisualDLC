-- Админ-панель: флаг заморозки. Бан хранится в auth.users (banned_until).
-- Выполнить в Supabase: SQL Editor → New query → Run.

alter table public.profiles
  add column if not exists frozen boolean not null default false;
