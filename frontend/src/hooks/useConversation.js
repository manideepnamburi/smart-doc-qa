// useConversation.js
//
// A "custom hook" -- just a regular JavaScript function whose name starts
// with "use" and which calls other hooks (useState, useEffect) inside it.
// This lets us bundle "all the state and logic for a conversation" into
// one reusable unit, instead of cramming it directly into App.jsx.

import { useState, useEffect } from "react";
import { askQuestion } from "../api";

const STORAGE_KEY = "smartdocqa_conversation";

// Read whatever was saved last time, or start with an empty conversation.
// This function runs ONCE, only when the component first mounts (see the
// useState(loadInitialMessages) call below) -- not on every render.
function loadInitialMessages() {
  try {
    const saved = localStorage.getItem(STORAGE_KEY);
    return saved ? JSON.parse(saved) : [];
  } catch {
    // If localStorage has garbage in it for any reason, don't crash the
    // whole app -- just start fresh.
    return [];
  }
}

export function useConversation() {
  // Passing a FUNCTION to useState (not a value) means React only calls
  // loadInitialMessages() once, on the very first render -- not on every
  // re-render. If we wrote useState(loadInitialMessages()) instead (calling
  // it immediately), it would re-read localStorage on every single render,
  // which is wasteful and unnecessary.
  const [messages, setMessages] = useState(loadInitialMessages);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState(null);

  // ── useEffect: our second core React concept ──────────────────────────
  // useState handles "data that changes." useEffect handles "do something
  // in response to that change, outside of rendering" -- here, that
  // "something" is writing to localStorage, which isn't part of what gets
  // shown on screen, so it doesn't belong in the render logic itself.
  //
  // The array at the end, [messages], is the "dependency list" -- it tells
  // React "only re-run this effect when `messages` changes," not on every
  // single render for any reason. Get this list wrong (e.g. leave it off
  // entirely) and the effect runs after EVERY render, which is a very
  // common React bug.
  useEffect(() => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(messages));
  }, [messages]);

  // The second parameter here -- { fallbackToLLM = false } = {} -- is a
  // common JS pattern: "an options object, destructured, with a default
  // for both the property AND the whole object." It means ALL of these
  // are valid calls: sendMessage("text"), sendMessage("text", {}), and
  // sendMessage("text", { fallbackToLLM: true }). Using an options object
  // (instead of a plain second argument) pays off once a function has
  // several optional settings -- callers can pass just the ones they care
  // about, by name, instead of remembering positional argument order.
  async function sendMessage(questionText, { fallbackToLLM = false } = {}) {
    if (!questionText.trim()) return;

    // Immediately show the user's own question in the list, before the API
    // call even starts -- this is what makes a chat UI feel responsive
    // instead of frozen while waiting on the network.
    const userMessage = { role: "user", content: questionText };
    setMessages((prev) => [...prev, userMessage]);
    // ^ [...prev, userMessage] means "a NEW array containing everything in
    // the old array, plus this one new item." React state should never be
    // mutated directly (never prev.push(...)) -- always build a new array/
    // object and hand THAT to the setter.

    setIsLoading(true);
    setError(null);

    try {
      // Build the history payload from the conversation SO FAR (before this
      // new question). Messages alternate user, assistant, user, assistant...
      // so we walk through in pairs, matching the backend's ConversationTurn
      // shape exactly: { question, answer }.
      const history = [];
      for (let i = 0; i < messages.length - 1; i++) {
        if (messages[i].role === "user" && messages[i + 1].role === "assistant") {
          history.push({
            question: messages[i].content,
            answer: messages[i + 1].content,
          });
        }
      }

      const result = await askQuestion(questionText, history, fallbackToLLM);

      const assistantMessage = {
        role: "assistant",
        content: result.answer,
        citations: result.citations ?? [],
        answerSource: result.answerSource,
      };
      setMessages((prev) => [...prev, assistantMessage]);
    } catch (err) {
      setError(err.message);
      // Show the error AS a message in the conversation too, so the user
      // sees what happened instead of a silently stuck loading spinner.
      setMessages((prev) => [
        ...prev,
        { role: "assistant", content: `Something went wrong: ${err.message}`, isError: true },
      ]);
    } finally {
      setIsLoading(false);
    }
  }

  function clearConversation() {
    setMessages([]);
  }

  // What this hook exposes to whatever component uses it -- App.jsx never
  // needs to know HOW any of this works, just that it can read `messages`,
  // call `sendMessage(text)`, and check `isLoading`.
  return { messages, sendMessage, isLoading, error, clearConversation };
}
