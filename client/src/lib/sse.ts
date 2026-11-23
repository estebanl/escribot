export interface SseMessage {
  event: string;
  data: string;
}

/**
 * Reads a `text/event-stream` response body (including `POST` streams) and invokes
 * `onMessage` for each complete SSE frame. Comment keepalive lines (`: ...`) are ignored.
 */
export async function readSseStream(
  body: ReadableStream<Uint8Array>,
  onMessage: (message: SseMessage) => void
): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder("utf-8");
  let buffer = "";
  let currentEvent = "message";
  let dataLines: string[] = [];

  const flushFrame = () => {
    if (dataLines.length === 0) {
      currentEvent = "message";
      return;
    }
    const data = dataLines.join("\n");
    const event = currentEvent;
    currentEvent = "message";
    dataLines = [];
    onMessage({ event, data });
  };

  const processLine = (rawLine: string) => {
    const line = rawLine.endsWith("\r") ? rawLine.slice(0, -1) : rawLine;
    if (line === "") {
      flushFrame();
      return;
    }
    if (line.startsWith(":")) {
      return;
    }
    const colon = line.indexOf(":");
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) {
      value = value.slice(1);
    }

    if (field === "event") {
      currentEvent = value || "message";
    } else if (field === "data") {
      dataLines.push(value);
    }
  };

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";
    for (const line of lines) {
      processLine(line);
    }
  }

  buffer += decoder.decode();
  if (buffer.length > 0) {
    processLine(buffer);
  }
  flushFrame();
}
