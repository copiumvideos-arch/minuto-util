// Minuto Útil relay: the Telegram bot's webhook. It turns the owner's taps on "Publicar" / "Rejeitar" (under a
// review video) into a run of .github/workflows/publish.yml, and "/novo [English article]" into a run of render.yml.
// It holds no state and posts nothing itself. Everything else, and anyone but the owner, is ignored.
//
// Secrets (wrangler secret put): TELEGRAM_BOT_TOKEN, TELEGRAM_WEBHOOK_SECRET (also given to Telegram's setWebhook),
// OWNER_CHAT_ID, GITHUB_TOKEN (fine-grained, this repository only, "Actions: read and write"). Var: GITHUB_REPO.

const CALLBACK = /^(pub|rej):(\d{8}-\d{4})$/;
const NEW_VIDEO = /^\/novo(?:@\w+)?(?:\s+(.+))?$/s;

export function createWorker(fetchImpl = (...args) => fetch(...args)) {
  return {
    async fetch(request, rawEnv) {
      // Secrets pasted or piped into wrangler can carry a trailing newline.
      const env = Object.fromEntries(Object.entries(rawEnv).map(([k, v]) => [k, typeof v === "string" ? v.trim() : v]));
      const url = new URL(request.url);
      if (request.method !== "POST" || url.pathname !== "/telegram") return new Response("not found", { status: 404 });
      if (!sameSecret(request.headers.get("X-Telegram-Bot-Api-Secret-Token") ?? "", env.TELEGRAM_WEBHOOK_SECRET ?? "")) {
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
        await handleUpdate(update, env, fetchImpl);
      } catch (error) {
        console.log(`update ${update?.update_id} failed: ${error?.message}`);
      }
      return new Response("ok");
    },
  };
}

export default createWorker();

export async function handleUpdate(update, env, fetchImpl) {
  const owner = String(env.OWNER_CHAT_ID ?? "");
  if (!owner) return;

  const query = update?.callback_query;
  if (query) {
    if (String(query.from?.id) !== owner || String(query.message?.chat?.id) !== owner) {
      await telegram(env, fetchImpl, "answerCallbackQuery", { callback_query_id: query.id });
      return;
    }
    const match = CALLBACK.exec(query.data ?? "");
    if (!match) {
      await telegram(env, fetchImpl, "answerCallbackQuery", { callback_query_id: query.id, text: "Botão desconhecido." });
      return;
    }
    const action = match[1] === "pub" ? "publish" : "reject";
    const status = await dispatch(env, fetchImpl, "publish.yml", { tag: match[2], action });
    if (status !== 204) {
      await telegram(env, fetchImpl, "answerCallbackQuery", {
        callback_query_id: query.id, text: `Não consegui iniciar (GitHub ${status}). Tente de novo.`, show_alert: true,
      });
      return;
    }
    await telegram(env, fetchImpl, "answerCallbackQuery", {
      callback_query_id: query.id, text: action === "publish" ? "Publicando..." : "Rejeitando...",
    });
    // The buttons go away so the same video is not sent twice by a second tap.
    await telegram(env, fetchImpl, "editMessageReplyMarkup", {
      chat_id: query.message.chat.id, message_id: query.message.message_id, reply_markup: { inline_keyboard: [] },
    });
    return;
  }

  const message = update?.message;
  if (message && String(message.chat?.id) === owner && String(message.from?.id) === owner) {
    const match = NEW_VIDEO.exec((message.text ?? "").trim());
    if (!match) return;
    const article = (match[1] ?? "").trim();
    if (article.length > 200 || /[\u0000-\u001f]/.test(article)) {
      await telegram(env, fetchImpl, "sendMessage", { chat_id: message.chat.id, text: "Use /novo ou /novo <artigo da Wikipedia em inglês>." });
      return;
    }
    const status = await dispatch(env, fetchImpl, "render.yml", { article });
    const text = status === 204
      ? `Gerando um vídeo${article ? ` sobre ${article}` : " com o próximo tema da fila"}. Chega aqui em uns 20 minutos.`
      : `Não consegui iniciar (GitHub ${status}).`;
    await telegram(env, fetchImpl, "sendMessage", { chat_id: message.chat.id, text });
  }
}

async function dispatch(env, fetchImpl, workflow, inputs) {
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

async function telegram(env, fetchImpl, method, body) {
  await fetchImpl(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/${method}`, {
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
