import { watchServerWake } from "@/lib/server-wake";

// A module of its own so main.tsx can import it first: creating the router (in router.tsx)
// already starts the first page's requests, and those must be watched too.
watchServerWake();
