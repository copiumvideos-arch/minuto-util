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

test("Refazer starts render.yml remaking that run and removes the buttons", async () => {
  const { calls, worker } = fakeNetwork();
  await worker.fetch(post(tap("redo:20261027-1959")), env);
  assert.equal(calls[0].url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/render.yml/dispatches");
  assert.deepEqual(calls[0].body.inputs, { article: "", remake: "20261027-1959" });
  assert.ok(calls.some((c) => c.url.endsWith("/editMessageReplyMarkup")));
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

const message = (text, from = 1000000001) => ({ update_id: 2, message: { text, from: { id: from }, chat: { id: from } } });
const githubCalls = (calls) => calls.filter((c) => c.url.startsWith("https://api.github.com/"));

test("/novo starts render.yml now, with the idea when one is given", async () => {
  let net = fakeNetwork();
  await net.worker.fetch(post(message("/novo")), env);
  assert.equal(net.calls[0].url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/render.yml/dispatches");
  assert.deepEqual(net.calls[0].body.inputs, { article: "" });
  assert.ok(net.calls[1].body.text.includes("próximo tema"));

  net = fakeNetwork();
  await net.worker.fetch(post(message("/novo por que só o Brasil usa urna eletrônica?")), env);
  assert.deepEqual(net.calls[0].body.inputs, { article: "por que só o Brasil usa urna eletrônica?" });

  net = fakeNetwork();
  await net.worker.fetch(post(message(`/novo ${"x".repeat(300)}`)), env);
  assert.equal(githubCalls(net.calls).length, 0);
});

test("a plain text or /ideia queues the idea for the next video through ideas.yml", async () => {
  for (const text of ["por que só o Brasil usa urna eletrônica?", "/ideia por que só o Brasil usa urna eletrônica?", "/adicionar por que só o Brasil usa urna eletrônica?"]) {
    const net = fakeNetwork();
    await net.worker.fetch(post(message(text)), env);
    const [github] = githubCalls(net.calls);
    assert.equal(github.url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/ideas.yml/dispatches");
    assert.deepEqual(github.body.inputs, { action: "add", date: "", index: "", idea: "por que só o Brasil usa urna eletrônica?", item: "" });
    assert.ok(net.calls.at(-1).body.text.startsWith("Procurando um tema"));
  }
  for (const text of ["/ideia", "/start", `${"x".repeat(201)}`]) {
    const net = fakeNetwork();
    await net.worker.fetch(post(message(text)), env);
    assert.equal(githubCalls(net.calls).length, 0, text);
    assert.ok(net.calls.at(-1).body.text.startsWith("Mande um tema"));
  }
  const net = fakeNetwork();
  await net.worker.fetch(post(message("por que só o Brasil usa urna eletrônica?", 5555)), env);
  assert.equal(net.calls.length, 0);
});

test("/fila, /mover and /remover change the queue through ideas.yml", async () => {
  const cases = [
    ["/fila", { action: "queue", item: "", index: "" }],
    ["/queue", { action: "queue", item: "", index: "" }],
    ["/mover 5 2", { action: "move", item: "5", index: "2" }],
    ["/remover 3", { action: "remove", item: "3", index: "" }],
  ];
  for (const [text, expected] of cases) {
    const net = fakeNetwork();
    await net.worker.fetch(post(message(text)), env);
    const [github] = githubCalls(net.calls);
    assert.equal(github.url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/ideas.yml/dispatches", text);
    assert.deepEqual(github.body.inputs, { ...expected, date: "", idea: "" }, text);
  }
  for (const text of ["/mover 5", "/mover cinco dois", "/remover", "/fila agora"]) {
    const net = fakeNetwork();
    await net.worker.fetch(post(message(text)), env);
    assert.ok(!githubCalls(net.calls).some((c) => c.body.inputs.action !== undefined && c.body.inputs.action !== "add"), text);
  }
});

test("the queue buttons move an item to the top or remove it by its key, and retire the old list", async () => {
  for (const [data, action] of [["qt:b-sinking-of-the-titanic", "top"], ["qd:a-20261027-200000-text", "remove"]]) {
    const { calls, worker } = fakeNetwork();
    await worker.fetch(post(tap(data)), env);
    const [github] = githubCalls(calls);
    assert.equal(github.url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/ideas.yml/dispatches");
    assert.deepEqual(github.body.inputs, { action, date: "", index: "", idea: "", item: data.slice(3) });
    assert.deepEqual(calls.find((c) => c.url.endsWith("/editMessageReplyMarkup")).body.reply_markup, { inline_keyboard: [] });
  }
  const forged = fakeNetwork();
  await forged.worker.fetch(post(tap("qd:../../etc")), env);
  assert.equal(githubCalls(forged.calls).length, 0);
});

test("tapping an idea approves it through ideas.yml and turns only that number into a check mark", async () => {
  const keyboard = [[1, 2, 3, 4, 5], [6, 7, 8, 9, 10]].map((row) => row.map((n) => ({ text: `${n}`, callback_data: `idea:20261028:${n}` })));
  const update = tap("idea:20261028:7");
  update.callback_query.message.reply_markup = { inline_keyboard: keyboard };
  const { calls, worker } = fakeNetwork();

  await worker.fetch(post(update), env);

  const [github] = githubCalls(calls);
  assert.equal(github.url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/ideas.yml/dispatches");
  assert.deepEqual(github.body.inputs, { action: "approve", date: "20261028", index: "7", idea: "", item: "" });
  const edit = calls.find((c) => c.url.endsWith("/editMessageReplyMarkup")).body.reply_markup.inline_keyboard;
  assert.deepEqual(edit[1][1], { text: "✅ 7", callback_data: "done" });
  assert.deepEqual(edit[0][0], { text: "1", callback_data: "idea:20261028:1" });

  const again = fakeNetwork();
  await again.worker.fetch(post(tap("done")), env);
  assert.equal(githubCalls(again.calls).length, 0);
  assert.ok(again.calls[0].body.text.includes("já está aprovada"));

  const forged = fakeNetwork();
  await forged.worker.fetch(post(tap("idea:2026-10-28:7")), env);
  assert.equal(githubCalls(forged.calls).length, 0);
});

test("the secret check compares whole strings", () => {
  assert.ok(sameSecret("abc", "abc"));
  assert.ok(!sameSecret("abd", "abc"));
  assert.ok(!sameSecret("ab", "abc"));
  assert.ok(!sameSecret("abcd", "abc"));
  assert.ok(!sameSecret("", ""));
});
