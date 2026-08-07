// useBranding.js
//
// A THIRD useEffect pattern, distinct from the one in useConversation:
//
//   useConversation's effect: "whenever `messages` CHANGES, save it."
//     -> dependency array is [messages]
//
//   This effect: "ONCE, when the component first appears, fetch a file."
//     -> dependency array is [] (empty) -- an empty array means "run this
//        effect exactly once, after the first render, and never again."
//        This is THE standard React pattern for "load something when the
//        page first opens" -- you'll see empty-array useEffect constantly
//        in real codebases.
//
// Why fetch this at runtime instead of just hardcoding the name/logo in
// JSX? Because branding.json lives in the public/ folder, which Vite
// copies to the build output AS-IS, unchanged. That means the exact same
// built app (same JS bundle) can be deployed for different companies --
// swapping their name/logo is just replacing branding.json and logo.svg
// in the deployed folder, no rebuild required.

import { useState, useEffect } from "react";

const DEFAULT_BRANDING = {
  appName: "SmartDocQA",
  tagline: "Evidence-grounded document Q&A",
  logoUrl: "/logo.svg",
};

export function useBranding() {
  const [branding, setBranding] = useState(DEFAULT_BRANDING);

  useEffect(() => {
    fetch("/branding.json")
      .then((res) => {
        if (!res.ok) throw new Error("branding.json not found");
        return res.json();
      })
      .then((data) => setBranding(data))
      .catch(() => {
        // If branding.json is missing or malformed, fall back to the
        // defaults already in state -- the app should never crash or show
        // a blank header just because a config file wasn't set up yet.
        console.warn("Could not load branding.json, using defaults.");
      });
  }, []); // <-- empty array: run once, on mount, never again

  // A second useEffect, reacting to `branding` changing (once the fetch
  // above resolves) -- updates the actual browser tab title. This is a
  // classic real-world useEffect use: syncing React state to something
  // OUTSIDE React entirely (the DOM's document object).
  useEffect(() => {
    document.title = branding.appName;
  }, [branding]);

  return branding;
}
