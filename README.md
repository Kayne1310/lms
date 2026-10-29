# UTC Attend

Personal UTC/Eduso attendance dashboard: Next.js UI, .NET 10 API, PostgreSQL and Hangfire.

## Start locally

Run the frontend in development mode (not Docker):

   `cd frontend && npm run dev -- -p 3003`

Open `http://localhost:3003`. The API listens on `http://localhost:8081`; PostgreSQL is external and is not started by this project.

After receiving your database connection string, set it in PowerShell, then start only the API:

   `$env:ConnectionStrings__Postgres = "<your PostgreSQL connection string>"`
   `docker compose up --build api`

Register or log in with the Eduso email and password. The API validates those credentials with Eduso before creating a UTC Attend session. The password is never returned to the browser or written to logs; an encrypted copy is kept using ASP.NET Core Data Protection so Hangfire can create fresh Eduso sessions for background sync and attendance. Preserve the Data Protection key ring across API restarts and deployments (by default under the API process user's `~/.aspnet/DataProtection-Keys/UtcAttendance` directory), or configure `DataProtection:KeysPath` to a durable, private directory. Existing accounts must log in once with their Eduso credentials to enable background jobs. Every dashboard action remains scoped to the account in the JWT; a new login invalidates the previous web session for the same email.

Configure a stable production JWT key with `AUTH__JWTSIGNINGKEY` (at least 32 characters).

The API's recurring sync runs every minute. It creates one scheduled Hangfire job per upcoming live session; results are written to the dashboard history. The Hangfire dashboard is deliberately not exposed publicly. If the API is deployed outside a personal machine, protect the API with real application authentication first.
