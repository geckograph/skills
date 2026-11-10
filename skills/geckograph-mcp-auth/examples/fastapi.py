"""The app's side of GeckoGraph's session bridge, in FastAPI. A shape to follow, not a drop-in:
`db`, `current_user`, `find_user_by_external_id`, `is_active` and `start_session_for` stand for the app's own code.

Config (server settings, per environment):
  GECKOGRAPH_PROVISION_KEY   the app's mcp:provision key (a secret: server config only)
  GECKOGRAPH_SESSION_URL     the session URL exactly as registered in GeckoGraph
  GECKOGRAPH_REVOKE_URL      the revoke URL exactly as registered
"""
import hashlib
import hmac
import json
import os

import httpx
from fastapi import APIRouter, Depends, Request, Response
from fastapi.responses import JSONResponse

from .app import current_user, db, find_user_by_external_id, is_active, start_session_for
from .verify_geckograph_jwt import is_geckograph_call  # the function in verify-jwt.md

GECKOGRAPH = "https://app.geckograph.com"
APP_SLUG = "your-app"

router = APIRouter()


def sha256_hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def no_link() -> JSONResponse:
    # The same answer for an unknown link, a wrong secret, another user's link and an inactive user.
    return JSONResponse({"error": "no_link"}, status_code=404)


# 1. The frontend posts the link request here, over the user's own session (onBridgeLinkRequest).
@router.post("/api/geckograph/bridge-links")
async def redeem_link(request: Request, user=Depends(current_user)):
    body = await request.json()
    link_request = body.get("request") if isinstance(body, dict) else None
    if not isinstance(link_request, str) or not link_request:
        return Response(status_code=400)
    # `user` is the SESSION's user. Never an id from the body.
    async with httpx.AsyncClient(timeout=10) as client:
        answer = await client.post(
            f"{GECKOGRAPH}/api/mcp/v1/apps/{APP_SLUG}/bridge-links/redeem",
            headers={"Authorization": f"Bearer {os.environ['GECKOGRAPH_PROVISION_KEY']}"},
            json={"request": link_request, "externalUserId": user.external_id},
        )
    if answer.status_code != 200:
        return Response(status_code=502)  # store nothing
    link = answer.json()
    # One row per link. Never replaces another link's row, not even this user's own.
    await db.execute(
        "INSERT INTO geckograph_bridge_links (link_id, user_id, secret_sha256) VALUES ($1, $2, $3)",
        link["linkId"], user.id, sha256_hex(link["secret"]),
    )
    return Response(status_code=204)  # never return the secret to the browser


# 2. GeckoGraph asks for a session. Raw body first: the JWT holds the hash of these exact bytes.
@router.post("/api/geckograph/bridge/session")
async def bridge_session(request: Request):
    raw = await request.body()
    if not is_geckograph_call(request.headers.get("authorization"), raw, os.environ["GECKOGRAPH_SESSION_URL"]):
        return Response(status_code=401)
    try:
        body = json.loads(raw)
    except ValueError:
        return Response(status_code=400)
    if not isinstance(body, dict):
        return Response(status_code=400)
    external_user_id, link_id, secret = body.get("externalUserId"), body.get("linkId"), body.get("secret")
    if not all(isinstance(v, str) for v in (external_user_id, link_id, secret)):
        return Response(status_code=400)

    user = await find_user_by_external_id(external_user_id)
    if user is None:
        return no_link()
    row = await db.fetchrow(
        "SELECT secret_sha256 FROM geckograph_bridge_links WHERE link_id = $1 AND user_id = $2",
        link_id, user.id,
    )
    if row is None or not hmac.compare_digest(row["secret_sha256"], sha256_hex(secret)):
        return no_link()
    if not is_active(user):
        return no_link()

    # A NEW session, from the same code the login runs after the password check.
    session = await start_session_for(user.id)
    return JSONResponse({
        "headers": session.headers,  # e.g. {"Authorization": "Bearer ..."}, or {}
        "cookies": [
            {"name": c.name, "value": c.value, "domain": c.domain, "path": c.path or "/"} for c in session.cookies
        ],
        "expiresAt": session.expires_at.strftime("%Y-%m-%dT%H:%M:%SZ"),  # UTC
    })


# 3. GeckoGraph revokes one link.
@router.post("/api/geckograph/bridge/revoke")
async def bridge_revoke(request: Request):
    raw = await request.body()
    if not is_geckograph_call(request.headers.get("authorization"), raw, os.environ["GECKOGRAPH_REVOKE_URL"]):
        return Response(status_code=401)
    try:
        body = json.loads(raw)
    except ValueError:
        return Response(status_code=400)
    if not isinstance(body, dict):
        return Response(status_code=400)
    if body.get("event") != "link.revoked" or not isinstance(body.get("externalUserId"), str) \
            or not isinstance(body.get("linkId"), str):
        return Response(status_code=400)
    user = await find_user_by_external_id(body["externalUserId"])
    if user is not None:
        await db.execute(
            "DELETE FROM geckograph_bridge_links WHERE link_id = $1 AND user_id = $2", body["linkId"], user.id,
        )
    return Response(status_code=204)  # also when it was already gone
