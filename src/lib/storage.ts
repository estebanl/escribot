import crypto from "crypto";
import { and, eq, sql } from "drizzle-orm";
import { del, get, put } from "@vercel/blob";
import type { Db } from "@/lib/db";
import * as schema from "@/lib/db/schema";
import { PLANS, type PlanId } from "@/lib/plans";

/** Largest single upload accepted (the client already compresses to WebP). */
export const MAX_IMAGE_BYTES = 2 * 1024 * 1024;

const ALLOWED_TYPES = new Set(["image/png", "image/jpeg", "image/webp", "image/gif"]);
const IMAGE_ROUTE_PREFIX = "/api/images/";

export class ImageUploadError extends Error {
  constructor(
    message: string,
    public status: 400 | 413 = 400
  ) {
    super(message);
  }
}

function isBlobStorageConfigured(): boolean {
  return Boolean(process.env.BLOB_READ_WRITE_TOKEN?.trim());
}

function parseDataUrl(dataUrl: string): { contentType: string; data: Buffer } {
  const match = dataUrl.match(/^data:([a-z]+\/[a-z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+)$/i);
  if (!match) throw new ImageUploadError("The image could not be read. Try a PNG, JPEG or WebP file.");
  const contentType = match[1].toLowerCase();
  if (!ALLOWED_TYPES.has(contentType)) {
    throw new ImageUploadError("Only PNG, JPEG, WebP or GIF images can be uploaded.");
  }
  return { contentType, data: Buffer.from(match[2], "base64") };
}

export async function getStorageUsedBytes(db: Db, userId: string): Promise<number> {
  const [row] = await db
    .select({ total: sql<number>`coalesce(sum(${schema.images.bytes}), 0)` })
    .from(schema.images)
    .where(eq(schema.images.userId, userId));
  return Number(row?.total ?? 0);
}

/**
 * Stores an uploaded image (data URL) for `userId` and returns the URL the
 * app uses to display it. Enforces the per-image size limit and the plan's
 * storage allowance.
 */
export async function storeImage(
  db: Db,
  userId: string,
  plan: PlanId,
  dataUrl: string
): Promise<string> {
  const { contentType, data } = parseDataUrl(dataUrl);
  if (data.length === 0) throw new ImageUploadError("The image is empty.");
  if (data.length > MAX_IMAGE_BYTES) {
    throw new ImageUploadError("Images must be 2 MB or smaller.", 413);
  }

  const allowance = PLANS[plan].storageBytes;
  if (allowance !== null) {
    const used = await getStorageUsedBytes(db, userId);
    if (used + data.length > allowance) {
      throw new ImageUploadError(
        `You have used your ${Math.round(allowance / 1024 / 1024)} MB of image storage. Delete some uploaded prompts or upgrade your plan.`,
        413
      );
    }
  }

  const id = crypto.randomUUID();
  const now = new Date().toISOString();

  if (isBlobStorageConfigured()) {
    const ext = contentType.split("/")[1].replace("jpeg", "jpg");
    const blob = await put(`users/${userId}/images/${id}.${ext}`, data, {
      access: "private",
      contentType,
      addRandomSuffix: false,
    });
    await db.insert(schema.images).values({
      id,
      userId,
      contentType,
      bytes: data.length,
      storage: "blob",
      blobPathname: blob.pathname,
      dataBase64: null,
      createdAt: now,
    });
  } else {
    await db.insert(schema.images).values({
      id,
      userId,
      contentType,
      bytes: data.length,
      storage: "db",
      blobPathname: null,
      dataBase64: data.toString("base64"),
      createdAt: now,
    });
  }

  return `${IMAGE_ROUTE_PREFIX}${id}`;
}

async function readImage(row: typeof schema.images.$inferSelect): Promise<Buffer | null> {
  if (row.storage === "db") {
    return row.dataBase64 ? Buffer.from(row.dataBase64, "base64") : null;
  }
  if (!row.blobPathname) return null;
  const result = await get(row.blobPathname, { access: "private" });
  if (!result || result.statusCode !== 200) return null;
  return Buffer.from(await new Response(result.stream).arrayBuffer());
}

/** Loads one of the user's images; null when it does not exist or is not theirs. */
export async function getOwnedImage(db: Db, userId: string, imageId: string) {
  const [row] = await db
    .select()
    .from(schema.images)
    .where(and(eq(schema.images.id, imageId), eq(schema.images.userId, userId)));
  if (!row) return null;
  const data = await readImage(row);
  return data ? { contentType: row.contentType, data } : null;
}

/**
 * Resolves a prompt's image reference to a data URL for AI vision calls.
 * Inline data URLs (older uploads) are returned as-is; stored images are
 * loaded for their owner. Anything else (e.g. static files) returns null.
 */
export async function resolveImageForAI(
  db: Db,
  userId: string,
  ref: string | null | undefined
): Promise<string | null> {
  if (!ref) return null;
  if (ref.startsWith("data:")) return ref;
  if (!ref.startsWith(IMAGE_ROUTE_PREFIX)) return null;
  const image = await getOwnedImage(db, userId, ref.slice(IMAGE_ROUTE_PREFIX.length));
  return image ? `data:${image.contentType};base64,${image.data.toString("base64")}` : null;
}

/** Deletes every image a user owns (account deletion). */
export async function deleteUserImages(db: Db, userId: string) {
  const rows = await db
    .select({ blobPathname: schema.images.blobPathname })
    .from(schema.images)
    .where(eq(schema.images.userId, userId));
  const pathnames = rows.map((r) => r.blobPathname).filter((p): p is string => Boolean(p));
  if (pathnames.length > 0) await del(pathnames);
  await db.delete(schema.images).where(eq(schema.images.userId, userId));
}

/**
 * Moves an inline (data URL) image saved before object storage existed into
 * storage. Returns the new reference, or null if it could not be moved.
 * Storage limits are not applied: the data is already held.
 */
export async function migrateInlineImage(
  db: Db,
  userId: string,
  dataUrl: string
): Promise<string | null> {
  try {
    return await storeImage(db, userId, "owner", dataUrl);
  } catch {
    return null;
  }
}
