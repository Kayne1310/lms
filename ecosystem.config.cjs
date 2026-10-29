const path = require("node:path");

const root = __dirname;
const npmCommand = process.platform === "win32" ? "npm.cmd" : "npm";

module.exports = {
  apps: [
    {
      name: "utc-attendance-api",
      cwd: path.join(root, "backend", "publish"),
      script: "dotnet",
      args: ["UtcAttendance.Api.dll"],
      interpreter: "none",
      autorestart: true,
      restart_delay: 3000,
      max_restarts: 10,
      env_production: {
        ASPNETCORE_ENVIRONMENT: "Production",
      },
    },
    {
      name: "utc-attendance-web",
      cwd: path.join(root, "frontend"),
      script: npmCommand,
      args: ["run", "start", "--", "--hostname", "0.0.0.0", "--port", "3003"],
      interpreter: "none",
      autorestart: true,
      restart_delay: 3000,
      max_restarts: 10,
      env_production: {
        NODE_ENV: "production",
        PORT: "3003",
      },
    },
  ],
};
