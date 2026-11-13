/**
 * Option B, derived secrets (contract.md, 2b): the app's side of GeckoGraph's session bridge with NO link table.
 * Only the three routes that differ from express.ts are here; the widget session route (0) is the same.
 * `requireSignedIn`, `findUserByExternalId`, `isActive` and `startSessionFor` stand for the app's own code.
 *
 * Config (server settings, per environment), as in express.ts, plus:
 *   GECKOGRAPH_BRIDGE_KEY         32 random bytes, base64 (a secret: server config only). Changing it ends every link.
 */
import { createHmac, randomUUID, timingSafeEqual } from 'node:crypto';
import express, { type Request, type Response } from 'express';
import { isGeckoGraphCall } from './verify-geckograph-jwt'; // the function in verify-jwt.md
import { requireSignedIn, findUserByExternalId, isActive, startSessionFor } from './app';

const GECKOGRAPH = 'https://app.geckograph.com';
const APP_SLUG = 'your-app';

function bridgeKey(): Buffer {
  const key = Buffer.from(process.env.GECKOGRAPH_BRIDGE_KEY ?? '', 'base64');
  if (key.length < 32) throw new Error('GECKOGRAPH_BRIDGE_KEY is missing or short'); // fail closed, never accept
  return key;
}

/** The link's secret: only the app can make it, and it binds the link to one user. */
const derivedSecret = (linkId: string, externalUserId: string) =>
  createHmac('sha256', bridgeKey()).update(`geckograph-bridge-link\n${linkId}\n${externalUserId}`).digest('base64url');

// The link id is always a lowercase UUID (GeckoGraph refuses any other at redeem). Checking it also keeps the HMAC
// input unambiguous: a UUID holds no newline, so no other (linkId, externalUserId) pair can produce the same input.
const LINK_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** A parsed JSON body that is an object (not null, an array or a primitive), or null. */
const jsonObject = (raw: Buffer): Record<string, unknown> | null => {
  try {
    const v = JSON.parse(raw.toString('utf8'));
    return v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null;
  } catch { return null; }
};

const sameText = (a: string, b: string) => {
  const x = Buffer.from(a, 'utf8'), y = Buffer.from(b, 'utf8');
  return x.length === y.length && timingSafeEqual(x, y);
};

export const geckograph = express.Router();

// 1. The frontend posts the link request here, over the user's own session (onBridgeLinkRequest).
geckograph.post('/api/geckograph/bridge-links', express.json(), requireSignedIn, async (req: Request, res: Response) => {
  const request = typeof req.body?.request === 'string' ? req.body.request : '';
  if (!request) return res.sendStatus(400);
  const user = req.user!; // the SESSION's user. Never an id from the body.

  const linkId = randomUUID();
  const redeemed = await fetch(`${GECKOGRAPH}/api/mcp/v1/apps/${APP_SLUG}/bridge-links/redeem`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${process.env.GECKOGRAPH_PROVISION_KEY}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ request, externalUserId: user.externalId, linkId, secret: derivedSecret(linkId, user.externalId) }),
  });
  if (redeemed.status !== 200) return res.sendStatus(502);
  return res.sendStatus(204); // nothing to store; never return the secret to the browser
});

// 2. GeckoGraph asks for a session. Raw body first: the JWT holds the hash of these exact bytes.
geckograph.post('/api/geckograph/bridge/session', express.raw({ type: '*/*', limit: '16kb' }), async (req, res) => {
  const raw: Buffer = Buffer.isBuffer(req.body) ? req.body : Buffer.alloc(0);
  if (!(await isGeckoGraphCall(req.get('authorization'), raw, process.env.GECKOGRAPH_SESSION_URL!))) {
    return res.sendStatus(401);
  }
  const body = jsonObject(raw);
  if (!body) return res.sendStatus(400);
  const { externalUserId, linkId, secret } = body;
  if (typeof externalUserId !== 'string' || typeof linkId !== 'string' || typeof secret !== 'string') {
    return res.sendStatus(400);
  }

  const noLink = () => res.status(404).json({ error: 'no_link' }); // the same answer for every reason
  if (!LINK_ID.test(linkId) || !sameText(derivedSecret(linkId, externalUserId), secret)) return noLink();
  const user = await findUserByExternalId(externalUserId);
  if (!user || !isActive(user)) return noLink();

  // A NEW session, from the same code the login runs after the password check.
  const session = await startSessionFor(user.id);
  return res.status(200).json({
    headers: session.headers,
    cookies: session.cookies.map((c) => ({ name: c.name, value: c.value })),
    expiresAt: session.expiresAt.toISOString(),
  });
});

// 3. GeckoGraph revokes one link. There is no record to delete: GeckoGraph drops its copy of the secret.
geckograph.post('/api/geckograph/bridge/revoke', express.raw({ type: '*/*', limit: '16kb' }), async (req, res) => {
  const raw: Buffer = Buffer.isBuffer(req.body) ? req.body : Buffer.alloc(0);
  if (!(await isGeckoGraphCall(req.get('authorization'), raw, process.env.GECKOGRAPH_REVOKE_URL!))) {
    return res.sendStatus(401);
  }
  return res.sendStatus(204);
});

// Mount the router BEFORE any app-wide express.json(), or the two GeckoGraph routes lose their raw body.
