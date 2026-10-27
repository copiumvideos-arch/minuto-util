import { test } from "node:test";
import assert from "node:assert/strict";
import { createWorker, sameSecret } from "../src/worker.js";

const env = {
  TELEGRAM_BOT_TOKEN: "123:bot-secret",
  TELEGRAM_WEBHOOK_SECRET: "hook-secret",
  OWNER_CHAT_ID: "1000000001",
  GITHUB_TOKEN: "github_pat_x",
  GITHUB_REPO: "owner/minuto-util",
};

// GitHub and Telegram replaced: every call is recorded, GitHub answers with `githubStatus`.
function fakeNetwork(githubStatus = 204) {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, body: JSON.parse(init.body), headers: init.headers });
    return new Response(null, { status: url.startsWith("https://api.github.com/") ? githubStatus : 200 });
  };
  return { calls, worker: createWorker(fetchImpl) };
}

function post(update, secret = "hook-secret", path = "/telegram") {
  return new Request(`https://relay.example.workers.dev${path}`, {
    method: "POST",
    headers: { "X-Telegram-Bot-Api-Secret-Token": secret, "Content-Type": "application/json" },
    body: JSON.stringify(update),
  });
}

const tap = (data, from = 1000000001) => ({
  update_id: 1,
  callback_query: { id: "cq1", data, from: { id: from }, message: { message_id: 77, chat: { id: from } } },
});

test("Publicar starts publish.yml for that run and removes the buttons", async () => {
  const { calls, worker } = fakeNetwork();
  const response = await worker.fetch(post(tap("pub:20261027-1800")), env);

  assert.equal(response.status, 200);
  const github = calls.find((c) => c.url.startsWith("https://api.github.com/"));
  assert.equal(github.url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/publish.yml/dispatches");
  assert.deepEqual(github.body, { ref: "main", inputs: { tag: "20261027-1800", action: "publish" } });
  assert.equal(github.headers.Authorization, "Bearer github_pat_x");
  assert.ok(calls.some((c) => c.url.endsWith("/answerCallbackQuery") && c.body.text === "Publicando..."));
  const edit = calls.find((c) => c.url.endsWith("/editMessageReplyMarkup"));
  assert.deepEqual(edit.body, { chat_id: 1000000001, message_id: 77, reply_markup: { inline_keyboard: [] } });
});

test("Rejeitar starts publish.yml with the reject action", async () => {
  const { calls, worker } = fakeNetwork();
  await worker.fetch(post(tap("rej:20261027-1800")), env);
  assert.deepEqual(calls[0].body.inputs, { tag: "20261027-1800", action: "reject" });
});

test("a GitHub error keeps the buttons and tells the owner", async () => {
  const { calls, worker } = fakeNetwork(401);
  await worker.fetch(post(tap("pub:20261027-1800")), env);
  assert.ok(calls.some((c) => c.url.endsWith("/answerCallbackQuery") && c.body.text.includes("GitHub 401")));
  assert.ok(!calls.some((c) => c.url.endsWith("/editMessageReplyMarkup")));
});

test("anyone but the owner, a forged button or a wrong secret starts nothing", async () => {
  for (const request of [
    post(tap("pub:20261027-1800", 5555)),
    post(tap("pub:20261027-1800; rm -rf /")),
    post(tap("pub:../../x")),
    post(tap("pub:20261027-1800"), "wrong-secret"),
    post(tap("pub:20261027-1800"), ""),
    post(tap("pub:20261027-1800"), "hook-secret", "/other"),
  ]) {
    const { calls, worker } = fakeNetwork();
    await worker.fetch(request, env);
    assert.ok(!calls.some((c) => c.url.startsWith("https://api.github.com/")), "GitHub was called");
  }
  const { worker } = fakeNetwork();
  assert.equal((await worker.fetch(post(tap("pub:20261027-1800"), "wrong-secret"), env)).status, 403);
});

test("/novo starts render.yml, with an article when one is given", async () => {
  const message = (text, from = 1000000001) => ({ update_id: 2, message: { text, from: { id: from }, chat: { id: from } } });
  let net = fakeNetwork();
  await net.worker.fetch(post(message("/novo")), env);
  assert.equal(net.calls[0].url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/render.yml/dispatches");
  assert.deepEqual(net.calls[0].body.inputs, { article: "" });
  assert.ok(net.calls[1].body.text.includes("próximo tema"));

  net = fakeNetwork();
  await net.worker.fetch(post(message("/novo Battle of Hastings")), env);
  assert.deepEqual(net.calls[0].body.inputs, { article: "Battle of Hastings" });

  for (const text of ["oi", `/novo ${"x".repeat(300)}`]) {
    net = fakeNetwork();
    await net.worker.fetch(post(message(text)), env);
    assert.ok(!net.calls.some((c) => c.url.startsWith("https://api.github.com/")));
  }
  net = fakeNetwork();
  await net.worker.fetch(post(message("/novo", 5555)), env);
  assert.equal(net.calls.length, 0);
});

test("the secret check compares whole strings", () => {
  assert.ok(sameSecret("abc", "abc"));
  assert.ok(!sameSecret("abd", "abc"));
  assert.ok(!sameSecret("ab", "abc"));
  assert.ok(!sameSecret("abcd", "abc"));
  assert.ok(!sameSecret("", ""));
});
