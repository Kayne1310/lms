import type { NextConfig } from "next";
import fs from "node:fs";
import path from "node:path";

function getApiProxyTarget() {
  if (process.env.API_PROXY_TARGET) return process.env.API_PROXY_TARGET;

  const settingsPath = path.resolve(
    process.cwd(),
    "../backend/UtcAttendance.Api/appsettings.json",
  );
  if (!fs.existsSync(settingsPath)) return "http://127.0.0.1:8081";

  const settings = JSON.parse(fs.readFileSync(settingsPath, "utf8"));
  const endpoint = settings.Kestrel?.Endpoints?.Http?.Url;
  return endpoint ? new URL(endpoint).origin : "http://127.0.0.1:8081";
}

const nextConfig: NextConfig = {
  output: "standalone",
  async rewrites() {
    const apiTarget = getApiProxyTarget();
    return [{ source: "/api/:path*", destination: `${apiTarget}/api/:path*` }];
  },
  cacheComponents: true,
  partialPrefetching: true,
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
