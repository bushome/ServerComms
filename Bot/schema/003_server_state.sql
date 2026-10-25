-- ServerComms schema, version 3: servers.state ('starting' while a map's world is loading, 'online'
-- once it runs), so Discord can show Starting instead of Offline during long boots.
ALTER TABLE servers ADD COLUMN IF NOT EXISTS state VARCHAR(16) NOT NULL DEFAULT 'online';
