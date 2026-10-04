# Maison Rizlène

Site de présentation privé, inspiré de la maquette dans `Maquettes/`. Un administrateur crée un projet et importe un MP4. Le client reçoit un code aléatoire de 256 bits ; seul son SHA-256 est conservé. La vidéo est stockée dans un bucket Supabase privé, tandis que les métadonnées et l'état du visionnage sont dans PostgreSQL.

## Mise en place Supabase

1. Créer un projet gratuit dans le [tableau de bord Supabase](https://supabase.com/dashboard). Cette étape nécessite votre propre connexion et, le cas échéant, une vérification par e-mail.
2. Copier [`.env.example`](.env.example) vers `.env.local` et renseigner l'URL du projet, l'adresse e-mail administrateur et un [jeton personnel limité à ce projet](https://supabase.com/docs/guides/platform/personal-access-tokens) avec les permissions **Database Read/Write**, **API Keys Read** et **API Key Secrets Read**. Le script récupère les clés du projet. `.env.local` est ignoré par Git : ne transmettez pas le jeton dans le chat.
3. Dans Supabase **Authentication → URL Configuration**, ajouter `SITE_URL` aux URL de redirection autorisées. Pour les essais locaux, utiliser `http://127.0.0.1:5080/`.
4. Exécuter `./scripts/setup-supabase.ps1`. Le script applique [`supabase/schema.sql`](supabase/schema.sql), envoie une invitation à l'adresse admin et lui attribue le rôle dans la base. Le destinataire ouvre le lien d'invitation sur le site pour définir son mot de passe. Le script retire ensuite le jeton personnel de `.env.local`.

Les clients sont représentés par leurs projets et codes d'accès ; ils n'ont pas besoin d'un compte Auth. **Alternative sans jeton personnel :** inviter d'abord `maison.rizlene@gmail.com` dans **Authentication → Users**, puis exécuter [`supabase/schema.sql`](supabase/schema.sql) une seule fois dans **SQL Editor**. Le script SQL attribue le rôle à cette adresse. Copier ensuite les clés **publishable** et **secret** depuis **Settings → API Keys** dans `.env.local` ; ne pas les partager dans le chat.

## Lancement local

Après configuration de `.env.local`, exécuter :

```powershell
./scripts/start-local.ps1
```

Ouvrir l'URL affichée. L'administration est accessible par le lien en pied de page ou avec `/#admin`.

## Déploiement gratuit

Pousser le dépôt sur GitHub puis créer un **Blueprint Render** à partir de [`render.yaml`](render.yaml), ou un Web Service Render gratuit avec le [`Dockerfile`](Dockerfile). Saisir les trois variables Supabase dans les paramètres du service. Render fournit HTTPS et un sous-domaine `onrender.com`.

Le plan gratuit Render s'endort après une période d'inactivité. Supabase Free a des limites de stockage et de trafic ; la vidéo est limitée ici à 25 Mo. Pour une prestation commerciale avec disponibilité garantie, prévoir ensuite une offre payante adaptée.

## Sécurité et limites réelles

- Les tables ont RLS activé et aucun accès `anon` ou `authenticated` ; seul le serveur utilise la clé `secret`.
- La connexion admin utilise Supabase Auth et un cookie HTTP-only, Secure et SameSite Strict. Les mutations vérifient un jeton antifalsification. Les tentatives de code et de connexion sont limitées par adresse IP.
- Le code n'est montré qu'à la création. La première validation le consomme atomiquement, même si la lecture échoue ou si le client ferme la page. Un ticket de 30 secondes autorise une unique requête vidéo ; le fichier est servi sans cache et sans URL Supabase publique.
- Le MP4 doit être encodé pour la lecture progressive (`faststart` recommandé), avec un codec lisible dans les navigateurs, tel que H.264/AAC.
- **Aucun site web ne peut garantir le blocage des captures, de l'enregistrement d'écran, d'une caméra externe ou de la copie des octets vidéo par une personne techniquement compétente.** La lecture unique et le stockage privé limitent l'accès ordinaire, mais ne protègent pas l'idée montrée contre toute reproduction. Pour réduire davantage le risque, montrer un extrait volontairement incomplet ou filigrané, et réserver les détails de réalisation au contrat.

Le visuel `hero-interior.png` a été généré avec l'outil ImageGen à partir de la maquette comme référence : salon chaleureux, drapé brun, canapé crème, aucune interface ou typographie intégrée.
