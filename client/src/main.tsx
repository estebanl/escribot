// First, before the router module starts the first page's requests.
import "@/lib/watch-server-wake";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { RouterProvider } from "react-router/dom";
import { ServerWakeNotice } from "@/components/ServerWakeNotice";
import { router } from "@/router";
import "./globals.css";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <RouterProvider router={router} />
  </StrictMode>
);

// A root of its own: the router renders nothing until the first page's data arrives,
// which is exactly when the notice is needed.
const notice = document.body.appendChild(document.createElement("div"));
createRoot(notice).render(
  <StrictMode>
    <ServerWakeNotice />
  </StrictMode>
);
