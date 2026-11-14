import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  serverExternalPackages: ["@libsql/client"],
  // SQL migrations are read from disk at boot (src/lib/db/index.ts), so they
  // must be shipped with every server bundle.
  outputFileTracingIncludes: {
    "/*": ["./drizzle/**/*"],
  },
  turbopack: {
    rules: {
      "*.css": {
        loaders: ["@tailwindcss/turbopack"],
        as: "*.css",
      },
    },
  },
};

export default nextConfig;
