/**
 * The app's side of GeckoGraph's session bridge, in Express. A shape to follow, not a drop-in:
 * `db`, `requireSignedIn`, `findUserByExternalId`, `isActive` and `startSessionFor` stand for the app's own code.
 *
 * Config (server settings, per environment):
 *   GECKOGRAPH_PROVISION_KEY      the app's mcp:provision key (a secret: server config only)
 *   GECKOGRAPH_ENVIRONMENT        the bridge environment's name in GeckoGraph (e.g. staging, production)
 *   GECKOGRAPH_SESSION_URL        the session URL exactly as registered in GeckoGraph
 *   GECKOGRAPH_REVOKE_URL         the revoke URL exactly as registered
 */
import { createHash, timingSafeEqual } from 'node:crypto';
import express, { type Request, type Response } from 'express';
import { isGeckoGraphCall } from './verify-geckograph-jwt'; // the function in verify-jwt.md
import { db, requireSignedIn, findUserByExternalId, isActive, startSessionFor } from './app';

const GECKOGRAPH = 'https://app.geckograph.com';
const APP_SLUG = 'your-app';

const sha256Hex = (text: string) => createHash('sha256').update(text, 'utf8').digest('hex');
const sameHex = (a: string, b: string) =>
  a.length === b.length && timingSafeEqual(Buffer.from(a, 'utf8'), Buffer.from(b, 'utf8'));

export const geckograph = express.Router();

// 0. The panel page asks for a widget session for the signed-in user, and passes it to GeckoGraph.mountMcpPanel.
//    It lasts an hour: the page calls this again before expiresAt and passes the new one to panel.setWidgetSession.
geckograph.post('/api/geckograph/widget-session', requireSignedIn, async (req: Request, res: Response) => {
  const user = req.user!; // the SESSION's user. Never an id from the body.
  const created = await fetch(`${GECKOGRAPH}/api/mcp/v1/apps/${APP_SLUG}/widget-sessions`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${process.env.GECKOGRAPH_PROVISION_KEY}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ externalUserId: user.externalId, environment: process.env.GECKOGRAPH_ENVIRONMENT }),
  });
  // 403 domain_unverified or headless_off, 409 no_origins: a dashboard setting is missing (contract.md, 1a).
  if (created.status !== 201) return res.sendStatus(502);
  const { widgetSession, expiresAt } = (await created.json()) as { widgetSession: string; expiresAt: string };
  return res.json({ widgetSession, expiresAt });
});

// 1. The frontend posts the link request here, over the user's own session (onBridgeLinkRequest).
geckograph.post('/api/geckograph/bridge-links', express.json(), requireSignedIn, async (req: Request, res: Response) => {
  const request = typeof req.body?.request === 'string' ? req.body.request : '';
  if (!request) return res.sendStatus(400);
  const user = req.user!; // the SESSION's user. Never an id from the body.

  const redeemed = await fetch(`${GECKOGRAPH}/api/mcp/v1/apps/${APP_SLUG}/bridge-links/redeem`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${process.env.GECKOGRAPH_PROVISION_KEY}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ request, externalUserId: user.externalId }),
  });
  if (redeemed.status !== 200) return res.sendStatus(502); // store nothing
  const { linkId, secret } = (await redeemed.json()) as { linkId: string; secret: string };

  // One row per link. Never replaces another link's row, not even this user's own.
  await db.query(
    'INSERT INTO geckograph_bridge_links (link_id, user_id, secret_sha256) VALUES ($1, $2, $3)',
    [linkId, user.id, sha256Hex(secret)],
  );
  return res.sendStatus(204); // never return the secret to the browser
});

// 2. GeckoGraph asks for a session. Raw body first: the JWT holds the hash of these exact bytes.
geckograph.post('/api/geckograph/bridge/session', express.raw({ type: '*/*', limit: '16kb' }), async (req, res) => {
  const raw: Buffer = Buffer.isBuffer(req.body) ? req.body : Buffer.alloc(0);
  if (!(await isGeckoGraphCall(req.get('authorization'), raw, process.env.GECKOGRAPH_SESSION_URL!))) {
    return res.sendStatus(401);
  }
  let body: { externalUserId?: unknown; linkId?: unknown; secret?: unknown };
  try { body = JSON.parse(raw.toString('utf8')); } catch { return res.sendStatus(400); }
  if (body === null || typeof body !== 'object' || Array.isArray(body)) return res.sendStatus(400); // not a JSON object
  const { externalUserId, linkId, secret } = body;
  if (typeof externalUserId !== 'string' || typeof linkId !== 'string' || typeof secret !== 'string') {
    return res.sendStatus(400);
  }

  const noLink = () => res.status(404).json({ error: 'no_link' }); // the same answer for every reason
  const user = await findUserByExternalId(externalUserId);
  if (!user) return noLink();
  const { rows } = await db.query(
    'SELECT secret_sha256 FROM geckograph_bridge_links WHERE link_id = $1 AND user_id = $2',
    [linkId, user.id],
  );
  if (rows.length === 0 || !sameHex(rows[0].secret_sha256, sha256Hex(secret))) return noLink();
  if (!isActive(user)) return noLink();

  // A NEW session, from the same code the login runs after the password check.
  const session = await startSessionFor(user.id);
  return res.status(200).json({
    headers: session.headers, // e.g. { Authorization: 'Bearer …' } (always `Scheme value`), or {}
    cookies: session.cookies.map((c) => ({ name: c.name, value: c.value })), // GeckoGraph reads only name and value
    expiresAt: session.expiresAt.toISOString(),
  });
});

// 3. GeckoGraph revokes one link.
geckograph.post('/api/geckograph/bridge/revoke', express.raw({ type: '*/*', limit: '16kb' }), async (req, res) => {
  const raw: Buffer = Buffer.isBuffer(req.body) ? req.body : Buffer.alloc(0);
  if (!(await isGeckoGraphCall(req.get('authorization'), raw, process.env.GECKOGRAPH_REVOKE_URL!))) {
    return res.sendStatus(401);
  }
  let body: { event?: unknown; externalUserId?: unknown; linkId?: unknown };
  try { body = JSON.parse(raw.toString('utf8')); } catch { return res.sendStatus(400); }
  if (body === null || typeof body !== 'object' || Array.isArray(body)) return res.sendStatus(400); // not a JSON object
  if (body.event !== 'link.revoked' || typeof body.externalUserId !== 'string' || typeof body.linkId !== 'string') {
    return res.sendStatus(400);
  }
  const user = await findUserByExternalId(body.externalUserId);
  if (user) {
    await db.query('DELETE FROM geckograph_bridge_links WHERE link_id = $1 AND user_id = $2', [body.linkId, user.id]);
  }
  return res.sendStatus(204); // also when it was already gone
});

// Mount the router BEFORE any app-wide express.json(), or the two GeckoGraph routes lose their raw body.
