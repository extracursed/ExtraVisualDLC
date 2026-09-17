-- Таблица одноразовых кодов для сброса пароля через Telegram.
-- Выполнить один раз в Supabase: SQL Editor → New query → Run.
-- Доступ только через service_role (RLS включён, публичных политик нет).

create table if not exists public.password_reset_codes (
  id bigint generated always as identity primary key,
  email text not null,
  code_hash text not null,
  expires_at timestamptz not null,
  consumed boolean not null default false,
  attempts int not null default 0,
  created_at timestamptz not null default now()
);

create index if not exists idx_prc_email on public.password_reset_codes (email);
create index if not exists idx_prc_expires on public.password_reset_codes (expires_at);

alter table public.password_reset_codes enable row level security;
