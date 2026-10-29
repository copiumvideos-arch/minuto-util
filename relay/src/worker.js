// Minuto Útil relay: the webhook of every channel's Telegram bot. It turns the owner's taps on "Publicar" /
// "Rejeitar" (under a review video) into a run of .github/workflows/publish.yml, taps on the daily idea list and text
// ideas into runs of ideas.yml, and "/novo [idea]" into a run of render.yml, always for the channel whose bot it was.
// It holds no state and posts nothing itself. Everything else, and anyone but the owner, is ignored.
//
// Each channel's bot has its own address and secrets (wrangler secret put):
//   minuto-util, as first deployed: /telegram with TELEGRAM_BOT_TOKEN and TELEGRAM_WEBHOOK_SECRET.
//   any other channel <id>: /telegram/<id> with TELEGRAM_BOT_TOKEN_<ID> and TELEGRAM_WEBHOOK_SECRET_<ID>, where <ID> is
//   the id in upper case with - as _ (profissao-spoiler: TELEGRAM_BOT_TOKEN_PROFISSAO_SPOILER).
// The webhook secret is also given to Telegram's setWebhook (relay/setup.sh). Shared: OWNER_CHAT_ID (a channel may
// have its own OWNER_CHAT_ID_<ID>), GITHUB_TOKEN (fine-grained, this repository only, "Actions: read and write").
// Var: GITHUB_REPO.

const CALLBACK = /^(pub|rej|redo|sok|snew):(\d{8}-\d{4})$/;
const RUN_TAG_IN_TEXT = /Execução (\d{8}-\d{4})/;
const SCRIPT_MARKER = "📝 Roteiro para revisar";
const SETTINGS = { "/pausar": "pause", "/pause": "pause", "/retomar": "resume", "/resume": "resume", "/revisar on": "review-on", "/revisar off": "review-off" };
const INFO = { "/status": "status", "/stats": "stats" };
const NEW_VIDEO = /^\/novo(?:@\w+)?(?:\s+(.+))?$/s;
const IDEA_COMMAND = /^\/(?:ideia|adicionar|add)(?:@\w+)?(?:\s+(.+))?$/s;
const QUEUE = /^\/(?:fila|queue)(?:@\w+)?$/;
const MOVE = /^\/(?:mover|move)(?:@\w+)?\s+(\d{1,2})\s+(\d{1,2})$/;
const REMOVE = /^\/(?:remover|remove)(?:@\w+)?\s+(\d{1,2})$/;
const QUEUE_BUTTON = /^(qt|qd):([0-9a-z-]{1,40})$/;
const IDEA = /^idea:(\d{8}):(\d{1,2})$/;
const DONE = "done";
const HELP = "Mande um tema ou uma pergunta (até 200 letras) para o próximo vídeo, ou /novo <tema> para fazer agora. " +
  "/fila mostra os próximos vídeos; /mover 5 2 muda a ordem; /remover 3 tira da fila; /status, /stats, /pausar, /retomar, /revisar on|off.";

const LEGACY_CHANNEL = "minuto-util";
const CHANNEL_PATH = /^\/telegram\/([a-z0-9](?:[a-z0-9-]{0,38}[a-z0-9])?)$/;

// The channel a webhook address belongs to, with its bot's secrets, or null for any other address. The id is checked
// before it names a secret, so a request can only reach the secrets of a channel that has them.
export function channelFor(pathname, env) {
  if (pathname === "/telegram") {
    return { id: LEGACY_CHANNEL, botToken: env.TELEGRAM_BOT_TOKEN, webhookSecret: env.TELEGRAM_WEBHOOK_SECRET, owner: env.OWNER_CHAT_ID };
  }
  const id = CHANNEL_PATH.exec(pathname)?.[1];
  if (!id || id === LEGACY_CHANNEL) return null;
  const suffix = id.toUpperCase().replaceAll("-", "_");
  return {
    id,
    botToken: env[`TELEGRAM_BOT_TOKEN_${suffix}`],
    webhookSecret: env[`TELEGRAM_WEBHOOK_SECRET_${suffix}`],
    owner: env[`OWNER_CHAT_ID_${suffix}`] || env.OWNER_CHAT_ID,
  };
}

export function createWorker(fetchImpl = (...args) => fetch(...args)) {
  return {
    async fetch(request, rawEnv) {
      // Secrets pasted or piped into wrangler can carry a trailing newline.
      const env = Object.fromEntries(Object.entries(rawEnv).map(([k, v]) => [k, typeof v === "string" ? v.trim() : v]));
      const url = new URL(request.url);
      const channel = request.method === "POST" ? channelFor(url.pathname, env) : null;
      if (!channel) return new Response("not found", { status: 404 });
      // A channel without its secrets (not set up yet) refuses everything, like a wrong secret.
      if (!sameSecret(request.headers.get("X-Telegram-Bot-Api-Secret-Token") ?? "", channel.webhookSecret ?? "")) {
        return new Response("forbidden", { status: 403 });
      }
      let update;
      try {
        update = await request.json();
      } catch {
        return new Response("ok");
      }
      // Always 200: Telegram would otherwise send the same update again and again.
      try {
        await handleUpdate(update, channel, env, fetchImpl);
      } catch (error) {
        console.log(`${channel.id} update ${update?.update_id} failed: ${error?.message}`);
      }
      return new Response("ok");
    },
  };
}

export default createWorker();

// One update from a channel's bot. Every workflow run it starts is for that channel, and every answer goes through
// that channel's bot.
export async function handleUpdate(update, channel, env, fetchImpl) {
  const owner = String(channel.owner ?? "");
  if (!owner) return;
  const dispatch = (workflow, inputs) => dispatchWorkflow(env, fetchImpl, workflow, { ...inputs, channel: channel.id });
  const telegram = (method, body) => callTelegram(channel.botToken, fetchImpl, method, body);
  const answerFailure = (query, status) => telegram("answerCallbackQuery", {
    callback_query_id: query.id, text: `Não consegui iniciar (GitHub ${status}). Tente de novo.`, show_alert: true,
  });

  const query = update?.callback_query;
  if (query) {
    if (String(query.from?.id) !== owner || String(query.message?.chat?.id) !== owner) {
      await telegram("answerCallbackQuery", { callback_query_id: query.id });
      return;
    }
    const data = query.data ?? "";
    if (data === DONE) {
      await telegram("answerCallbackQuery", { callback_query_id: query.id, text: "Essa já está aprovada." });
      return;
    }
    const queueButton = QUEUE_BUTTON.exec(data);
    if (queueButton) {
      const top = queueButton[1] === "qt";
      const status = await dispatch("ideas.yml", { action: top ? "top" : "remove", date: "", index: "", idea: "", item: queueButton[2] });
      if (status !== 204) {
        await answerFailure(query, status);
        return;
      }
      await telegram("answerCallbackQuery", {
        callback_query_id: query.id, text: top ? "Levando para o topo. A fila nova chega em um minuto." : "Tirando da fila. A fila nova chega em um minuto.",
      });
      // This list is out of date now; the updated one comes as a new message.
      await telegram("editMessageReplyMarkup", {
        chat_id: query.message.chat.id, message_id: query.message.message_id, reply_markup: { inline_keyboard: [] },
      });
      return;
    }
    const idea = IDEA.exec(data);
    if (idea) {
      const status = await dispatch("ideas.yml", { action: "approve", date: idea[1], index: idea[2], idea: "", item: "" });
      if (status !== 204) {
        await answerFailure(query, status);
        return;
      }
      await telegram("answerCallbackQuery", { callback_query_id: query.id, text: `Ideia ${idea[2]} aprovada ✅` });
      // The tapped number turns into a check mark; the others stay tappable.
      const rows = (query.message.reply_markup?.inline_keyboard ?? []).map((row) =>
        row.map((button) => (button.callback_data === data ? { text: `✅ ${idea[2]}`, callback_data: DONE } : button)));
      await telegram("editMessageReplyMarkup", {
        chat_id: query.message.chat.id, message_id: query.message.message_id, reply_markup: { inline_keyboard: rows },
      });
      return;
    }
    const match = CALLBACK.exec(data);
    if (!match) {
      await telegram("answerCallbackQuery", { callback_query_id: query.id, text: "Botão desconhecido." });
      return;
    }
    const kind = match[1];
    const tag = match[2];
    const status = kind === "redo" ? await dispatch("render.yml", { article: "", remake: tag })
      : kind === "sok" ? await dispatch("render.yml", { continue: tag, script: "", rescript: "false" })
      : kind === "snew" ? await dispatch("render.yml", { continue: tag, script: "", rescript: "true" })
      : await dispatch("publish.yml", { tag, action: kind === "pub" ? "publish" : "reject" });
    if (status !== 204) {
      await answerFailure(query, status);
      return;
    }
    const answers = {
      redo: "Refazendo: o novo vídeo chega em uns 15 minutos.",
      sok: "Roteiro aprovado: o vídeo chega em uns 15 minutos.",
      snew: "Escrevendo outro roteiro...",
      pub: "Publicando...",
      rej: "Rejeitando...",
    };
    await telegram("answerCallbackQuery", { callback_query_id: query.id, text: answers[kind] });
    // The buttons go away so the same video is not sent twice by a second tap.
    await telegram("editMessageReplyMarkup", {
      chat_id: query.message.chat.id, message_id: query.message.message_id, reply_markup: { inline_keyboard: [] },
    });
    return;
  }

  const message = update?.message;
  if (!message || String(message.chat?.id) !== owner || String(message.from?.id) !== owner) return;
  const text = (message.text ?? "").trim();
  if (!text) return;
  const reply = (body) => telegram("sendMessage", { chat_id: message.chat.id, text: body });

  // A reply to the bot's own message: to a script, the owner's edit; to a review video, its new title.
  const replied = message.reply_to_message;
  const repliedTag = replied?.from?.is_bot ? RUN_TAG_IN_TEXT.exec(replied.text ?? replied.caption ?? "")?.[1] : undefined;
  if (repliedTag) {
    if ((replied.text ?? "").startsWith(SCRIPT_MARKER)) {
      if (text.length > 3500) {
        await reply("O roteiro editado passou de 3500 letras; mande só as linhas que mudaram.");
        return;
      }
      const status = await dispatch("render.yml", { continue: repliedTag, script: text, rescript: "false" });
      await reply(status === 204 ? "✏️ Aplicando sua edição e fazendo o vídeo." : `Não consegui iniciar (GitHub ${status}).`);
      return;
    }
    if (replied.video || replied.caption) {
      if (text.length > 100 || /[\u0000-\u001f]/.test(text)) {
        await reply("O título pode ter até 100 letras, em uma linha.");
        return;
      }
      const status = await dispatch("ideas.yml", { action: "title", date: "", index: "", idea: text, item: repliedTag });
      await reply(status === 204 ? "🏷️ Trocando o título..." : `Não consegui trocar o título (GitHub ${status}).`);
      return;
    }
  }

  // Switches and reports.
  const command = text.toLowerCase().replace(/@\w+/, "").replace(/\s+/g, " ");
  if (SETTINGS[command] || INFO[command]) {
    const inputs = SETTINGS[command] ? { action: "settings", item: SETTINGS[command] } : { action: INFO[command], item: "" };
    const status = await dispatch("ideas.yml", { ...inputs, date: "", index: "", idea: "" });
    await reply(status === 204 ? "Um momento..." : `Não consegui (GitHub ${status}).`);
    return;
  }

  // The queue: show it, move an item (positions as /fila numbers them), remove one.
  const queueCommand = QUEUE.exec(text) ? { action: "queue", item: "", index: "" }
    : MOVE.exec(text) ? { action: "move", item: MOVE.exec(text)[1], index: MOVE.exec(text)[2] }
    : REMOVE.exec(text) ? { action: "remove", item: REMOVE.exec(text)[1], index: "" }
    : null;
  if (queueCommand) {
    const status = await dispatch("ideas.yml", { ...queueCommand, date: "", idea: "" });
    await reply(status === 204 ? "Um momento, a fila chega em um minuto." : `Não consegui abrir a fila (GitHub ${status}).`);
    return;
  }

  // "/novo [idea]": a video now. "/adicionar <idea>" (or /ideia, /add) or any plain text: first in line for the next video.
  const now = NEW_VIDEO.exec(text);
  const queued = now ? null : IDEA_COMMAND.exec(text) ?? (text.startsWith("/") ? null : [text, text]);
  if (!now && !queued) {
    await reply(HELP);
    return;
  }
  const idea = ((now ?? queued)[1] ?? "").trim();
  if (idea.length > 200 || /[\u0000-\u001f]/.test(idea) || (queued && !idea)) {
    await reply(HELP);
    return;
  }
  if (now) {
    const status = await dispatch("render.yml", { article: idea });
    await reply(status === 204
      ? `Gerando um vídeo${idea ? ` a partir de: ${idea}` : " com o próximo tema da fila"}. Chega aqui em uns 20 minutos.`
      : `Não consegui iniciar (GitHub ${status}).`);
    return;
  }
  const status = await dispatch("ideas.yml", { action: "add", date: "", index: "", idea, item: "" });
  await reply(status === 204 ? `Procurando um tema na Wikipedia para: ${idea}` : `Não consegui anotar (GitHub ${status}).`);
}

async function dispatchWorkflow(env, fetchImpl, workflow, inputs) {
  const response = await fetchImpl(`https://api.github.com/repos/${env.GITHUB_REPO}/actions/workflows/${workflow}/dispatches`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${env.GITHUB_TOKEN}`,
      Accept: "application/vnd.github+json",
      "X-GitHub-Api-Version": "2022-11-28",
      "User-Agent": "minuto-util-relay",
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ ref: "main", inputs }),
  });
  return response.status;
}

async function callTelegram(botToken, fetchImpl, method, body) {
  await fetchImpl(`https://api.telegram.org/bot${botToken}/${method}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}

// Compares in time that does not depend on where the strings differ.
export function sameSecret(given, expected) {
  if (!expected) return false;
  const a = new TextEncoder().encode(given);
  const b = new TextEncoder().encode(expected);
  let diff = a.length ^ b.length;
  for (let i = 0; i < b.length; i++) diff |= (a[i] ?? 0) ^ b[i];
  return diff === 0;
}
