try {
  const stored = localStorage.getItem("fe-theme");
  if (stored === "dark" || (!stored && window.matchMedia("(prefers-color-scheme: dark)").matches)) {
    document.documentElement.classList.add("dark");
  }
} catch {
  // Ignore storage access errors in restricted browsing contexts.
}
