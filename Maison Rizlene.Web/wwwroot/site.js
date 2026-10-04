const views = ['client-view', 'video-view', 'admin-login-view', 'admin-dashboard-view', 'set-password-view'];
const $ = id => document.getElementById(id);
let csrfToken = '';
let inviteToken = '';
const inviteParams = new URLSearchParams(location.hash.slice(1));
if (['invite', 'recovery'].includes(inviteParams.get('type')) && inviteParams.get('access_token')) {
  inviteToken = inviteParams.get('access_token');
  history.replaceState(null, '', '/#set-password');
}

async function csrf() {
  const response = await fetch('/api/csrf', { credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) throw new Error('Impossible de préparer la connexion. Réessayez.');
  csrfToken = (await response.json()).token;
}

async function request(path, options = {}) {
  if (options.method && options.method !== 'GET' && !csrfToken) await csrf();
  const response = await fetch(path, {
    credentials: 'same-origin', cache: 'no-store', ...options,
    headers: { ...(options.method && options.method !== 'GET' ? { 'X-CSRF-TOKEN': csrfToken } : {}), ...options.headers }
  });
  if (!response.ok) {
    let detail;
    try { detail = (await response.json()).error; } catch { /* generic error */ }
    throw new Error(detail || (response.status === 429 ? 'Trop de tentatives. Réessayez plus tard.' : 'Une erreur est survenue. Réessayez.'));
  }
  const body = await response.text();
  return body ? JSON.parse(body) : null;
}

function show(view) {
  for (const id of views) $(id).hidden = id !== view;
  document.body.classList.toggle('is-admin', view.startsWith('admin'));
}

function errorAt(id, message) { const el = $(id); el.textContent = message || ''; el.hidden = !message; }
function busy(form, value) { form.querySelector('button[type=submit]').disabled = value; }

$('access-form').addEventListener('submit', async event => {
  event.preventDefault();
  const form = event.currentTarget;
  busy(form, true); errorAt('access-error', '');
  try {
    const data = await request('/api/access', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code: $('access-code').value }) });
    $('access-code').value = '';
    $('video-title').textContent = data.name;
    show('video-view');
    const video = $('preview-video');
    video.src = `/api/video/${encodeURIComponent(data.id)}?ticket=${encodeURIComponent(data.ticket)}`;
    video.load();
    video.play().catch(() => { $('video-message').textContent = 'Appuyez sur la vidéo pour lancer la lecture.'; });
  } catch (error) { errorAt('access-error', error.message); }
  finally { busy(form, false); }
});

$('preview-video').addEventListener('click', event => event.currentTarget.play().catch(() => {}));
$('preview-video').addEventListener('ended', () => {
  const video = $('preview-video'); video.removeAttribute('src'); video.load();
  $('video-message').textContent = 'Votre aperçu est terminé. Merci de votre visite.';
});
$('preview-video').addEventListener('error', () => { $('video-message').textContent = 'La vidéo est indisponible. Contactez Maison Rizlène.'; });

$('login-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = event.currentTarget; busy(form, true); errorAt('login-error', '');
  try {
    await request('/api/admin/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ email: $('admin-email').value, password: $('admin-password').value }) });
    csrfToken = '';
    $('admin-password').value = ''; await dashboard();
  } catch { errorAt('login-error', 'Identifiants invalides ou accès non autorisé.'); }
  finally { busy(form, false); }
});

$('set-password-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = event.currentTarget; busy(form, true); errorAt('set-password-error', '');
  try {
    await request('/api/admin/set-password', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ accessToken: inviteToken, password: $('new-password').value }) });
    inviteToken = ''; $('new-password').value = ''; location.hash = '#admin'; show('admin-login-view');
  } catch (error) { errorAt('set-password-error', error.message); }
  finally { busy(form, false); }
});

async function dashboard() {
  await request('/api/admin/me'); show('admin-dashboard-view'); await loadProjects();
}

async function loadProjects() {
  const list = $('project-list'); list.replaceChildren();
  const projects = await request('/api/admin/projects');
  $('project-count').textContent = `${projects.length} projet${projects.length > 1 ? 's' : ''}`;
  if (!projects.length) { const empty = document.createElement('p'); empty.className = 'empty-state'; empty.textContent = 'Aucun projet pour le moment.'; list.append(empty); return; }
  for (const project of projects) {
    const item = document.createElement('div'); item.className = 'project-item';
    const info = document.createElement('div');
    const title = document.createElement('strong'); title.textContent = project.name;
    const client = document.createElement('span'); client.textContent = `${project.clientName} · ${new Date(project.createdAt).toLocaleDateString('fr-FR')}`;
    const status = document.createElement('div'); status.className = `status${project.viewedAt ? ' used' : ''}`;
    status.textContent = project.viewedAt ? 'VISIONNÉ' : 'EN ATTENTE';
    info.append(title, client); item.append(info, status); list.append(item);
  }
}

$('project-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = event.currentTarget; busy(form, true); errorAt('project-error', ''); $('created-code').hidden = true;
  const file = $('project-video').files[0];
  if (!file || file.size > 25 * 1024 * 1024 || file.type !== 'video/mp4') { errorAt('project-error', 'Choisissez une vidéo MP4 de 25 Mo maximum.'); busy(form, false); return; }
  try {
    const created = await request('/api/admin/projects', { method: 'POST', body: new FormData(form) });
    $('new-code').textContent = created.code; $('created-code').hidden = false; form.reset(); await loadProjects();
  } catch (error) { errorAt('project-error', error.message); }
  finally { busy(form, false); }
});

$('copy-code').addEventListener('click', async () => {
  await navigator.clipboard.writeText($('new-code').textContent);
  $('copy-code').textContent = 'Copié !'; setTimeout(() => $('copy-code').textContent = 'Copier le code', 2000);
});
$('logout-button').addEventListener('click', async () => {
  errorAt('logout-error', '');
  try {
    await request('/api/admin/logout', { method: 'POST' });
    csrfToken = '';
    $('created-code').hidden = true;
    show('admin-login-view');
  } catch (error) { errorAt('logout-error', error.message); }
});

async function route() {
  if (location.hash === '#set-password') { show('set-password-view'); return; }
  if (location.hash === '#admin') {
    try { await dashboard(); } catch { show('admin-login-view'); }
  } else if (!$('video-view').hidden) { return; }
  else show('client-view');
}
window.addEventListener('hashchange', route);
route();
