import crypto from "crypto";

const ALGORITHM = "aes-256-gcm";

function getDerivedKey(): Buffer {
  const rawSecret =
    process.env.APP_SECRET ||
    process.env.APP_PASSCODE ||
    "fluentescritor-local-dev-default-secret-v1";
  return crypto.createHash("sha256").update(rawSecret).digest();
}

export function encryptString(plaintext: string): string {
  const key = getDerivedKey();
  const iv = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv(ALGORITHM, key, iv);
  const encrypted = Buffer.concat([cipher.update(plaintext, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();
  return `${iv.toString("base64")}:${tag.toString("base64")}:${encrypted.toString("base64")}`;
}

export function decryptString(payload: string): string | null {
  try {
    const parts = payload.split(":");
    if (parts.length !== 3) return null;
    const [ivB64, tagB64, dataB64] = parts;
    const key = getDerivedKey();
    const decipher = crypto.createDecipheriv(
      ALGORITHM,
      key,
      Buffer.from(ivB64, "base64")
    );
    decipher.setAuthTag(Buffer.from(tagB64, "base64"));
    const decrypted = Buffer.concat([
      decipher.update(Buffer.from(dataB64, "base64")),
      decipher.final(),
    ]);
    return decrypted.toString("utf8");
  } catch {
    return null;
  }
}

export function maskApiKey(key: string | undefined | null): string | null {
  if (!key || key.trim().length < 6) return null;
  const trimmed = key.trim();
  if (trimmed.length <= 10) {
    return `${trimmed.slice(0, 3)}••••${trimmed.slice(-2)}`;
  }
  return `${trimmed.slice(0, 5)}••••••${trimmed.slice(-4)}`;
}
