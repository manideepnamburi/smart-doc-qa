// useAppConfig.js
//
// Same shape as useBranding.js -- fetch a JSON file once on mount, fall
// back to safe defaults if it's missing. Kept as a SEPARATE hook rather
// than merged into useBranding, on purpose: branding (name/logo) and
// feature behavior (toggles/defaults) are different concerns that a
// deployment might want to change independently. Combining them into one
// file would mean touching visual identity every time you just want to
// flip a feature default, or vice versa.
//
// (If you notice this looks almost identical to useBranding.js -- you're
// right, and it's a legitimate small duplication. Worth extracting a
// shared `useFetchJson(url, defaults)` helper if a third config file ever
// shows up; two instances of the same shape isn't yet worth the extra
// abstraction, per the usual "rule of three" judgment call.)

import { useState, useEffect } from "react";

// These are the REAL safety net -- if config.json is missing, slow to
// load, or malformed, the app must still behave exactly as specified:
// citations hidden, general-knowledge fallback off, both toggles shown by
// default so a company has to deliberately configure them away.
const DEFAULT_CONFIG = {
  showCitationsToggle: true,
  showFallbackToggle: true,
  defaultShowCitations: false,
  defaultUseFallback: false,
};

export function useAppConfig() {
  const [config, setConfig] = useState(DEFAULT_CONFIG);

  useEffect(() => {
    fetch("/config.json")
      .then((res) => {
        if (!res.ok) throw new Error("config.json not found");
        return res.json();
      })
      .then((data) => setConfig(data))
      .catch(() => {
        console.warn("Could not load config.json, using defaults.");
      });
  }, []);

  return config;
}
