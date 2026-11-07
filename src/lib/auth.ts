import { SignJWT, jwtVerify } from "jose";

export const AUTH_COOKIE_NAME = "fe_session";

export function isPasscodeRequired(): boolean {
  return Boolean(process.env.APP_PASSCODE && process.env.APP_PASSCODE.trim().length > 0);
}

function getSigningSecret(): Uint8Array {
  const raw =
    process.env.APP_SECRET ||
    process.env.APP_PASSCODE ||
    "fluentescritor-local-dev-default-secret-v1";
  return new TextEncoder().encode(raw.padEnd(32, "_fluent_escritor_hmac_key_"));
}

export async function createSessionToken(): Promise<string> {
  return new SignJWT({ authenticated: true, role: "owner" })
    .setProtectedHeader({ alg: "HS256" })
    .setIssuedAt()
    .setExpirationTime("30d")
    .sign(getSigningSecret());
}

export async function verifySessionToken(token: string | undefined): Promise<boolean> {
  if (!isPasscodeRequired()) {
    return true;
  }
  if (!token) {
    return false;
  }
  try {
    const { payload } = await jwtVerify(token, getSigningSecret());
    return payload.authenticated === true;
  } catch {
    return false;
  }
}
