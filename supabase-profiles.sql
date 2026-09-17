-- Профили с настоящим порядковым UID: первый юзер = 1, второй = 2, ...
-- Выполнить один раз в Supabase: SQL Editor → New query → Run.

-- 1. Таблица профилей
create table if not exists public.profiles (
  id bigint generated always as identity primary key,
  user_id uuid not null unique references auth.users (id) on delete cascade,
  username text,
  role text not null default 'Пользователь',
  hwid text,
  telegram_chat_id bigint,
  created_at timestamptz not null default now()
);

create index if not exists idx_profiles_user on public.profiles (user_id);

alter table public.profiles enable row level security;

drop policy if exists "read own profile" on public.profiles;
create policy "read own profile"
  on public.profiles for select
  using (auth.uid() = user_id);

drop policy if exists "update own profile" on public.profiles;
create policy "update own profile"
  on public.profiles for update
  using (auth.uid() = user_id);

-- 2. Триггер: каждому новому юзеру — свой UID автоматически
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  insert into public.profiles (user_id, username)
  values (
    new.id,
    coalesce(new.raw_user_meta_data ->> 'username', split_part(new.email, '@', 1))
  )
  on conflict (user_id) do nothing;
  return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
  after insert on auth.users
  for each row execute function public.handle_new_user();

-- 3. Backfill: выдать UID тем, кто уже зарегистрирован (по дате регистрации)
insert into public.profiles (user_id, username)
select
  id,
  coalesce(raw_user_meta_data ->> 'username', split_part(email, '@', 1))
from auth.users
order by created_at asc
on conflict (user_id) do nothing;
