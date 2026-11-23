// Cloudflare Worker: forwards Telegram webhook POSTs to the Yandex Cloud VM.
// Deploy as a Worker, then set the Telegram webhook to:
//   https://<worker-name>.<subdomain>.workers.dev/webhook
//
// The VM still validates X-Telegram-Bot-Api-Secret-Token, so the public
// Worker URL only acts as a transport and does not bypass webhook auth.
export default {
  async fetch(request) {
    const url = new URL(request.url);
    if (request.method !== "POST" || url.pathname !== "/webhook") {
      return new Response("Not found", { status: 404 });
    }

    const body = await request.arrayBuffer();
    const headers = new Headers(request.headers);
    headers.delete("host");
    headers.delete("content-length");

    const response = await fetch("https://81-26-181-247.sslip.io/api/telegram/webhook", {
      method: "POST",
      headers,
      body
    });

    return new Response(response.body, {
      status: response.status,
      headers: response.headers
    });
  }
};
