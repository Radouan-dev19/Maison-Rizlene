-- À exécuter une fois dans Supabase SQL Editor.
create table if not exists public.admins (
    id uuid primary key references auth.users(id) on delete cascade,
    created_at timestamptz not null default now()
);

create table if not exists public.projects (
    id uuid primary key default gen_random_uuid(),
    name text not null check (char_length(name) between 2 and 120),
    client_name text not null check (char_length(client_name) between 2 and 120),
    access_code_hash text not null unique check (access_code_hash ~ '^[0-9a-f]{64}$'),
    video_path text not null unique,
    created_by uuid not null references public.admins(id),
    created_at timestamptz not null default now(),
    viewed_at timestamptz,
    session_hash text,
    session_expires_at timestamptz
);

create index if not exists projects_created_at_idx on public.projects (created_at desc);
alter table public.admins enable row level security;
alter table public.projects enable row level security;
revoke all on public.admins, public.projects from anon, authenticated;

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('project-previews', 'project-previews', false, 26214400, array['video/mp4'])
on conflict (id) do update set public = false, file_size_limit = excluded.file_size_limit,
    allowed_mime_types = excluded.allowed_mime_types;

create or replace function public.redeem_project(p_code_hash text, p_ticket_hash text)
returns table(id uuid, name text)
language sql security invoker set search_path = '' as $$
    update public.projects p
       set viewed_at = now(), session_hash = p_ticket_hash,
           session_expires_at = now() + interval '30 seconds'
     where p.access_code_hash = p_code_hash and p.viewed_at is null
       and p_ticket_hash ~ '^[0-9a-f]{64}$'
     returning p.id, p.name;
$$;

create or replace function public.consume_video(p_project_id uuid, p_ticket_hash text)
returns table(video_path text)
language sql security invoker set search_path = '' as $$
    update public.projects p
       set session_hash = null, session_expires_at = null
     where p.id = p_project_id and p.session_hash = p_ticket_hash
       and p.session_expires_at > now()
     returning p.video_path;
$$;

revoke execute on function public.redeem_project(text, text) from public, anon, authenticated;
revoke execute on function public.consume_video(uuid, text) from public, anon, authenticated;
grant execute on function public.redeem_project(text, text) to service_role;
grant execute on function public.consume_video(uuid, text) to service_role;

-- Après avoir créé l'utilisateur admin dans Authentication > Users :
insert into public.admins (id)
select id from auth.users where lower(email) = 'maison.rizlene@gmail.com'
on conflict (id) do nothing;
