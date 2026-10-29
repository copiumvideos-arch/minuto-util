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
  assert.deepEqual(github.body, { ref: "main", inputs: { tag: "20261027-1800", action: "publish", channel: "minuto-util" } });
  assert.equal(github.headers.Authorization, "Bearer github_pat_x");
  assert.ok(calls.some((c) => c.url.endsWith("/answerCallbackQuery") && c.body.text === "Publicando..."));
  const edit = calls.find((c) => c.url.endsWith("/editMessageReplyMarkup"));
  assert.deepEqual(edit.body, { chat_id: 1000000001, message_id: 77, reply_markup: { inline_keyboard: [] } });
});

test("Refazer starts render.yml remaking that run and removes the buttons", async () => {
  const { calls, worker } = fakeNetwork();
  await worker.fetch(post(tap("redo:20261027-1959")), env);
  assert.equal(calls[0].url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/render.yml/dispatches");
  assert.deepEqual(calls[0].body.inputs, { article: "", remake: "20261027-1959", channel: "minuto-util" });
  assert.ok(calls.some((c) => c.url.endsWith("/editMessageReplyMarkup")));
});

test("Rejeitar starts publish.yml with the reject action", async () => {
  const { calls, worker } = fakeNetwork();
  await worker.fetch(post(tap("rej:20261027-1800")), env);
  assert.deepEqual(calls[0].body.inputs, { tag: "20261027-1800", action: "reject", channel: "minuto-util" });
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
  assert.deepEqual(net.calls[0].body.inputs, { article: "", channel: "minuto-util" });
  assert.ok(net.calls[1].body.text.includes("próximo tema"));

  net = fakeNetwork();
  await net.worker.fetch(post(message("/novo por que só o Brasil usa urna eletrônica?")), env);
  assert.deepEqual(net.calls[0].body.inputs, { article: "por que só o Brasil usa urna eletrônica?", channel: "minuto-util" });

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
    assert.deepEqual(github.body.inputs, { action: "add", date: "", index: "", idea: "por que só o Brasil usa urna eletrônica?", item: "", channel: "minuto-util" });
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
    assert.deepEqual(github.body.inputs, { ...expected, date: "", idea: "", channel: "minuto-util" }, text);
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
    assert.deepEqual(github.body.inputs, { action, date: "", index: "", idea: "", item: data.slice(3), channel: "minuto-util" });
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
  assert.deepEqual(github.body.inputs, { action: "approve", date: "20261028", index: "7", idea: "", item: "", channel: "minuto-util" });
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

test("script buttons continue the run: approve makes the video, the other asks for a new script", async () => {
  for (const [data, rescript] of [["sok:20261028-1000", "false"], ["snew:20261028-1000", "true"]]) {
    const { calls, worker } = fakeNetwork();
    await worker.fetch(post(tap(data)), env);
    assert.equal(calls[0].url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/render.yml/dispatches");
    assert.deepEqual(calls[0].body.inputs, { continue: "20261028-1000", script: "", rescript, channel: "minuto-util" });
  }
});

test("a reply to the script is the owner's edit; a reply to a review video is its new title", async () => {
  const replyTo = (text, replied) => ({
    update_id: 3,
    message: { text, from: { id: 1000000001 }, chat: { id: 1000000001 }, reply_to_message: { from: { id: 99, is_bot: true }, ...replied } },
  });
  let net = fakeNetwork();
  await net.worker.fetch(post(replyTo("Gancho: Por que só o Brasil?", { text: "📝 Roteiro para revisar · Democracia\n...\nExecução 20261028-1000" })), env);
  assert.equal(net.calls[0].url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/render.yml/dispatches");
  assert.deepEqual(net.calls[0].body.inputs, { continue: "20261028-1000", script: "Gancho: Por que só o Brasil?", rescript: "false", channel: "minuto-util" });

  net = fakeNetwork();
  await net.worker.fetch(post(replyTo("A urna que mudou o Brasil", { video: { file_id: "v" }, caption: "🎬 Urna\n...Execução 20261028-1000." })), env);
  assert.equal(net.calls[0].url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/ideas.yml/dispatches");
  assert.deepEqual(net.calls[0].body.inputs, { action: "title", date: "", index: "", idea: "A urna que mudou o Brasil", item: "20261028-1000", channel: "minuto-util" });

  // A reply to someone else's message is just an idea; a title over 100 letters is refused.
  net = fakeNetwork();
  await net.worker.fetch(post({ update_id: 4, message: { text: "Magna Carta", from: { id: 1000000001 }, chat: { id: 1000000001 },
    reply_to_message: { from: { id: 1000000001, is_bot: false }, text: "Execução 20261028-1000" } } }), env);
  assert.equal(githubCalls(net.calls)[0].body.inputs.action, "add");
  net = fakeNetwork();
  await net.worker.fetch(post(replyTo("x".repeat(101), { video: {}, caption: "Execução 20261028-1000" })), env);
  assert.equal(githubCalls(net.calls).length, 0);
});

test("/status, /stats, /pausar, /retomar and /revisar run ideas.yml", async () => {
  const cases = [
    ["/status", { action: "status", item: "" }],
    ["/stats", { action: "stats", item: "" }],
    ["/pausar", { action: "settings", item: "pause" }],
    ["/retomar", { action: "settings", item: "resume" }],
    ["/revisar on", { action: "settings", item: "review-on" }],
    ["/revisar OFF", { action: "settings", item: "review-off" }],
  ];
  for (const [text, expected] of cases) {
    const net = fakeNetwork();
    await net.worker.fetch(post(message(text)), env);
    assert.deepEqual(githubCalls(net.calls)[0].body.inputs, { ...expected, date: "", index: "", idea: "", channel: "minuto-util" }, text);
  }
});

// A second channel's bot on the same Worker: its own address, bot token and webhook secret; the owner is shared.
const twoBots = {
  ...env,
  TELEGRAM_BOT_TOKEN_TEST_SPOILER: "456:spoiler-bot",
  TELEGRAM_WEBHOOK_SECRET_TEST_SPOILER: "spoiler-hook",
};

test("a second channel's bot starts runs for that channel and answers through its own bot", async () => {
  const { calls, worker } = fakeNetwork();
  const response = await worker.fetch(post(tap("pub:20261029-1400"), "spoiler-hook", "/telegram/test-spoiler"), twoBots);

  assert.equal(response.status, 200);
  const [github] = githubCalls(calls);
  assert.equal(github.url, "https://api.github.com/repos/owner/minuto-util/actions/workflows/publish.yml/dispatches");
  assert.deepEqual(github.body.inputs, { tag: "20261029-1400", action: "publish", channel: "test-spoiler" });
  const answers = calls.filter((c) => c.url.startsWith("https://api.telegram.org/"));
  assert.ok(answers.length > 0);
  assert.ok(answers.every((c) => c.url.startsWith("https://api.telegram.org/bot456:spoiler-bot/")));

  const net = fakeNetwork();
  await net.worker.fetch(post(message("/novo"), "spoiler-hook", "/telegram/test-spoiler"), twoBots);
  assert.deepEqual(githubCalls(net.calls)[0].body.inputs, { article: "", channel: "test-spoiler" });
});

test("Minuto Útil's webhook is unchanged with a second bot on the Worker", async () => {
  const { calls, worker } = fakeNetwork();
  await worker.fetch(post(message("/status")), twoBots);
  assert.equal(githubCalls(calls)[0].body.inputs.channel, "minuto-util");
  assert.ok(calls.filter((c) => c.url.startsWith("https://api.telegram.org/")).every((c) => c.url.startsWith("https://api.telegram.org/bot123:bot-secret/")));
});

test("one bot's secret never opens another bot's address, and unknown or bad addresses start nothing", async () => {
  for (const [secret, path, status] of [
    ["hook-secret", "/telegram/test-spoiler", 403],
    ["spoiler-hook", "/telegram", 403],
    ["hook-secret", "/telegram/minuto-util", 404],
    ["spoiler-hook", "/telegram/other-channel", 403],
    ["", "/telegram/other-channel", 403],
    ["spoiler-hook", "/telegram/Test-Spoiler", 404],
    ["spoiler-hook", "/telegram/test-spoiler/", 404],
    ["spoiler-hook", "/telegram/test_spoiler", 404],
  ]) {
    const { calls, worker } = fakeNetwork();
    const response = await worker.fetch(post(tap("pub:20261029-1400"), secret, path), twoBots);
    assert.equal(response.status, status, `${path} with ${secret || "no secret"}`);
    assert.equal(calls.length, 0, path);
  }
});

test("a channel may have its own owner chat; the shared one counts otherwise", async () => {
  const own = { ...twoBots, OWNER_CHAT_ID_TEST_SPOILER: "2000000002" };
  let net = fakeNetwork();
  await net.worker.fetch(post(message("/fila", 2000000002), "spoiler-hook", "/telegram/test-spoiler"), own);
  assert.equal(githubCalls(net.calls)[0].body.inputs.channel, "test-spoiler");
  net = fakeNetwork();
  await net.worker.fetch(post(message("/fila"), "spoiler-hook", "/telegram/test-spoiler"), own);
  assert.equal(githubCalls(net.calls).length, 0);
  net = fakeNetwork();
  await net.worker.fetch(post(message("/fila"), "spoiler-hook", "/telegram/test-spoiler"), twoBots);
  assert.equal(githubCalls(net.calls).length, 1);
});

test("the secret check compares whole strings", () => {
  assert.ok(sameSecret("abc", "abc"));
  assert.ok(!sameSecret("abd", "abc"));
  assert.ok(!sameSecret("ab", "abc"));
  assert.ok(!sameSecret("abcd", "abc"));
  assert.ok(!sameSecret("", ""));
});
