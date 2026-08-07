// api.js
//
// Every network call the app makes goes through here. Nothing else in the
// app should call fetch() directly -- this is the single seam where things
// like auth tokens (Phase 11) or tenant headers (Phase 10) get added later,
// touching exactly one file instead of every component that happens to
// call the API.

// import.meta.env is Vite's way of exposing environment variables to your
// code. Anything prefixed VITE_ in .env.local becomes available here --
// that prefix is a deliberate Vite safety rule (only VITE_-prefixed vars
// are exposed to the browser; anything else stays server-side-only).
const API_BASE_URL = import.meta.env.VITE_API_BASE_URL;

/**
 * Sends a question to SmartDocQA's query endpoint, along with recent
 * conversation history so follow-up questions ("what about women?") get
 * resolved correctly by the backend's history-aware query rewriter.
 *
 * @param {string} question - the user's current question
 * @param {Array<{question: string, answer: string}>} history - prior turns, oldest first
 * @param {boolean} fallbackToLLM - if true, Claude answers from general
 *   knowledge when nothing relevant is found in the documents; if false,
 *   the system reports "not found" instead. Defaults to false -- answers
 *   stay strictly document-grounded unless the caller opts in.
 * @returns {Promise<object>} the QAResult shape from the backend
 */
export async function askQuestion(question, history = [], fallbackToLLM = false) {
  const response = await fetch(`${API_BASE_URL}/api/documents/query`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      question,
      useReranking: true,
      fallbackToLLM,
      history: history.length > 0 ? history : undefined,
    }),
  });

  if (!response.ok) {
    // A non-2xx response (guardrail rejection still returns 200 with a
    // "could not be processed" answer -- this branch is for genuine
    // failures: 500s, network-level issues the browser surfaces as a bad
    // status, etc.)
    throw new Error(`API request failed: ${response.status} ${response.statusText}`);
  }

  return response.json();
}
