# Verifying GeckoGraph's JWT

Every call to `sessionUrl` and `revokeUrl` carries `Authorization: Bearer <JWT>`, signed by GeckoGraph with ES256. The app holds no signing secret: it checks the token against GeckoGraph's public keys.

Configuration (server settings, per environment, never secrets):

| Setting | Value |
| --- | --- |
| issuer | `https://app.geckograph.com` |
| JWKS URL | `https://app.geckograph.com/oauth/jwks.json` |
| session audience | the session URL exactly as registered in GeckoGraph, e.g. `https://app.example.com/api/geckograph/bridge/session` |
| revoke audience | the revoke URL exactly as registered |

Compare `aud` to the configured string, never to a URL rebuilt from the incoming request: behind a proxy or a load balancer, the scheme, host or path the app sees differs from the registered one.

## The checks, in order

Each failure answers `401` with no body detail, before the body is parsed or any link is looked up.

1. **Read the raw body bytes** before any JSON middleware parses them. The hash is over the exact bytes sent: re-serialised JSON differs (key order, spacing, escapes).
2. **The header** is `Authorization: Bearer <token>`. Missing, another scheme, or empty: `401`.
3. **The JOSE header:** `alg` is exactly `ES256` (refuse `none`, `HS256` and every other value; never let the token choose the algorithm), and `typ` is exactly `geckograph-bridge+jwt`.
4. **The key:** find the JWKS key whose `kid` matches. Cache the JWKS (for example 10 minutes). On an unknown `kid`, fetch the JWKS again once (no more than once a minute), then refuse. The keys rotate with an overlap, so a refetch finds a new key.
5. **The signature** verifies with that key.
6. **`iss`** equals the issuer.
7. **`aud`** equals this endpoint's audience exactly: a session token is refused by `revokeUrl`, and the reverse.
8. **Time:** `exp` and `iat` are present numbers, `exp` is in the future, `iat` is not in the future, and `exp - iat <= 60`. Allow at most 5 seconds of clock skew.
9. **`body_sha256`** equals the lowercase hex SHA-256 of the raw body bytes.
10. **`jti`** is a non-empty string not seen before. Record it until the token's `exp` (plus the skew). A token seen again is refused, even with a good signature. Record a `jti` only after checks 2 to 9 passed, so a forged token can't fill the cache. With several app instances, keep the cache where all of them see it (Redis `SET jti 1 NX EX <seconds>`, or a table with a unique key); one instance may keep it in memory.

Then parse the body as JSON and handle it ([contract.md](contract.md)).

## TypeScript (Node, `jose`)

```ts
import { createHash } from 'node:crypto';
import { createRemoteJWKSet, jwtVerify, errors } from 'jose';

const ISSUER = 'https://app.geckograph.com';
const jwks = createRemoteJWKSet(new URL(`${ISSUER}/oauth/jwks.json`), {
  cacheMaxAge: 10 * 60_000,   // keep the keys 10 minutes
  cooldownDuration: 60_000,   // refetch on an unknown kid at most once a minute
});
const SKEW_SECONDS = 5;

/** Replay cache: one process. With several instances, use Redis SET NX EX or a unique-keyed table. */
const seenJti = new Map<string, number>();
function firstUse(jti: string, exp: number): boolean {
  const now = Math.floor(Date.now() / 1000);
  for (const [id, until] of seenJti) if (until < now) seenJti.delete(id);
  if (seenJti.has(jti)) return false;
  seenJti.set(jti, exp + SKEW_SECONDS);
  return true;
}

/** True when the request is GeckoGraph's, for this endpoint, with this exact body. */
export async function isGeckoGraphCall(
  authorization: string | undefined,
  rawBody: Buffer,
  audience: string,
): Promise<boolean> {
  const token = authorization?.startsWith('Bearer ') ? authorization.slice(7).trim() : '';
  if (!token) return false;
  try {
    const { payload } = await jwtVerify(token, jwks, {
      algorithms: ['ES256'],
      typ: 'geckograph-bridge+jwt',
      issuer: ISSUER,
      audience,
      clockTolerance: SKEW_SECONDS,
      requiredClaims: ['iat', 'exp', 'jti', 'body_sha256'],
    });
    const now = Math.floor(Date.now() / 1000);
    const { iat, exp, jti, body_sha256: bodyHash } = payload as Record<string, unknown>;
    if (typeof iat !== 'number' || typeof exp !== 'number' || exp - iat > 60) return false;
    if (iat > now + SKEW_SECONDS) return false;
    if (typeof aud(payload) !== 'string') return false;
    const actual = createHash('sha256').update(rawBody).digest('hex');
    if (typeof bodyHash !== 'string' || bodyHash !== actual) return false;
    if (typeof jti !== 'string' || !jti) return false;
    return firstUse(jti, exp);
  } catch (e) {
    if (e instanceof errors.JOSEError) return false;
    throw e;   // a JWKS fetch that failed is a 5xx, not a 401
  }
}

/** One audience only: a token naming several is not for this endpoint alone. */
function aud(payload: { aud?: string | string[] }) {
  return Array.isArray(payload.aud) ? undefined : payload.aud;
}
```

A JWKS fetch that fails (GeckoGraph unreachable) throws `JWKSTimeout` or a network error. Answering `5xx` there is right: GeckoGraph reads it as a fault and retries, and disconnects nobody. `jose` raises `JWKSNoMatchingKey` for an unknown `kid` after its refetch; that one is a `401`.

## Python (`PyJWT[crypto]`)

```python
import hashlib
import time

import jwt  # PyJWT, with the cryptography extra
from jwt import PyJWKClient

ISSUER = "https://app.geckograph.com"
SKEW_SECONDS = 5
_jwks = PyJWKClient(f"{ISSUER}/oauth/jwks.json", cache_keys=True, lifespan=600)

# Replay cache: one process. With several workers or instances, use Redis SET NX EX or a unique-keyed table.
_seen_jti: dict[str, float] = {}


def _first_use(jti: str, exp: int) -> bool:
    now = time.time()
    for key in [k for k, until in _seen_jti.items() if until < now]:
        del _seen_jti[key]
    if jti in _seen_jti:
        return False
    _seen_jti[jti] = exp + SKEW_SECONDS
    return True


def is_geckograph_call(authorization: str | None, raw_body: bytes, audience: str) -> bool:
    """True when the request is GeckoGraph's, for this endpoint, with this exact body."""
    if not authorization or not authorization.startswith("Bearer "):
        return False
    token = authorization[len("Bearer "):].strip()
    try:
        header = jwt.get_unverified_header(token)
        if header.get("alg") != "ES256" or header.get("typ") != "geckograph-bridge+jwt":
            return False
        key = _jwks.get_signing_key_from_jwt(token)  # refetches the JWKS on an unknown kid
        claims = jwt.decode(
            token,
            key.key,
            algorithms=["ES256"],
            issuer=ISSUER,
            audience=audience,
            leeway=SKEW_SECONDS,
            options={"require": ["iss", "aud", "iat", "exp", "jti", "body_sha256"]},
        )
    except jwt.PyJWKClientConnectionError:
        raise  # GeckoGraph's JWKS unreachable: answer 5xx, not 401
    except jwt.PyJWTError:
        return False
    if not isinstance(claims["aud"], str):
        return False
    iat, exp = claims["iat"], claims["exp"]
    if exp - iat > 60 or iat > time.time() + SKEW_SECONDS:
        return False
    if claims["body_sha256"] != hashlib.sha256(raw_body).hexdigest():
        return False
    jti = claims["jti"]
    if not isinstance(jti, str) or not jti:
        return False
    return _first_use(jti, exp)
```

## What not to do

- Don't parse the body first and hash `JSON.stringify(req.body)` / `json.dumps(body)`: the hash won't match GeckoGraph's, or worse, a changed body may match.
- Don't accept a token on one endpoint that was made for the other: check `aud` per endpoint.
- Don't skip `typ`: it is what tells this token apart from GeckoGraph's OAuth access tokens, signed by the same keys.
- Don't answer `404 {"error":"no_link"}` to a bad token: a bad token is `401`, so GeckoGraph never deletes a link because of a broken check.
- Don't log the token or the body.
