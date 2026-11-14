import crypto from "crypto";

const ALGORITHM = "aes-256-gcm";
const DEV_ONLY_SECRET = "fluentescritor-local-dev-default-secret-v1";

function deriveKey(secret: string): Buffer {
  return crypto.createHash("sha256").update(secret).digest();
}

/**
 * Key used to encrypt API keys saved in Settings. APP_SECRET is mandatory in
 * production; the built-in default only exists so local development works
 * out of the box.
 */
function getEncryptionKey(): Buffer {
  const secret = process.env.APP_SECRET?.trim();
  if (secret) return deriveKey(secret);
  if (process.env.NODE_ENV === "production") {
    throw new Error(
      "APP_SECRET is not configured. Set it to a long random value to store API keys."
    );
  }
  return deriveKey(DEV_ONLY_SECRET);
}

/**
 * Keys a stored value may have been encrypted with, newest first. Older
 * single-user installs derived the key from APP_PASSCODE or the default
 * secret; those are still accepted for reading so saved keys survive the
 * upgrade, and values are re-encrypted with APP_SECRET on the next save.
 */
function getDecryptionKeys(): Buffer[] {
  const secrets = [
    process.env.APP_SECRET?.trim(),
    process.env.APP_PASSCODE,
    DEV_ONLY_SECRET,
  ].filter((s): s is string => Boolean(s));
  return secrets.map(deriveKey);
}

export function encryptString(plaintext: string): string {
  const key = getEncryptionKey();
  const iv = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv(ALGORITHM, key, iv);
  const encrypted = Buffer.concat([cipher.update(plaintext, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();
  return `${iv.toString("base64")}:${tag.toString("base64")}:${encrypted.toString("base64")}`;
}

export function decryptString(payload: string): string | null {
  const parts = payload.split(":");
  if (parts.length !== 3) return null;
  const [ivB64, tagB64, dataB64] = parts;

  for (const key of getDecryptionKeys()) {
    try {
      const decipher = crypto.createDecipheriv(ALGORITHM, key, Buffer.from(ivB64, "base64"));
      decipher.setAuthTag(Buffer.from(tagB64, "base64"));
      const decrypted = Buffer.concat([
        decipher.update(Buffer.from(dataB64, "base64")),
        decipher.final(),
      ]);
      return decrypted.toString("utf8");
    } catch {
      // Wrong key or tampered data: try the next candidate.
    }
  }
  return null;
}

export function maskApiKey(key: string | undefined | null): string | null {
  if (!key || key.trim().length < 6) return null;
  const trimmed = key.trim();
  if (trimmed.length <= 10) {
    return `${trimmed.slice(0, 3)}••••${trimmed.slice(-2)}`;
  }
  return `${trimmed.slice(0, 5)}••••••${trimmed.slice(-4)}`;
}
