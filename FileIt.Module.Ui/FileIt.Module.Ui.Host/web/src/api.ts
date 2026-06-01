export type Flow = {
  id: number;
  application: string;
  invocationId: string;
  correlationId?: string;
  sourceContext: string;
  createdOn: string;
  level: string;
};

export type LogRow = {
  id: number;
  message: string;
  level: string;
  application: string;
  sourceContext: string;
  invocationId: string;
  correlationId?: string;
  eventId?: number;
  eventName?: string;
  createdOn: string;
};

export type DeadLetter = {
  deadLetterRecordId: number;
  messageId: string;
  sourceEntityName: string;
  failureCategory: string;
  status: string;
  deliveryCount: number;
  deadLetterReason?: string;
  deadLetteredTimeUtc: string;
  createdUtc: string;
};

export type DemoResult = {
  correlationId: string;
  fileName?: string;
  module: string;
  pattern: string;
};

async function getJson<T>(url: string): Promise<T> {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`${url}: ${r.status}`);
  return r.json();
}

async function postJson<T>(url: string): Promise<T> {
  const r = await fetch(url, { method: "POST" });
  if (!r.ok) throw new Error(`${url}: ${r.status}`);
  return r.json();
}

async function postForm<T>(url: string, form: FormData): Promise<T> {
  const r = await fetch(url, { method: "POST", body: form });
  if (!r.ok) throw new Error(`${url}: ${r.status}`);
  return r.json();
}

export const api = {
  recentFlows: (take = 50) => getJson<Flow[]>(`/api/flows?take=${take}`),
  flowTimeline: (correlationId: string) => getJson<LogRow[]>(`/api/flows/${correlationId}`),
  deadLetters: (take = 50) => getJson<DeadLetter[]>(`/api/deadletters?take=${take}`),
  dropCsv: () => postJson<DemoResult>("/api/demo/drop-csv"),
  sendApi: () => postJson<DemoResult>("/api/demo/send-api"),
  publishBroadcast: () => postJson<DemoResult>("/api/demo/publish-broadcast"),
  dropPoison: () => postJson<DemoResult>("/api/demo/drop-poison"),
  runSalesforce: () => postJson<DemoResult>("/api/demo/run-salesforce"),
  uploadFile: (file: File, module: string = "dataflow") => {
    const form = new FormData();
    form.append("file", file);
    form.append("module", module);
    return postForm<DemoResult>("/api/demo/upload", form);
  },
  replay: (id: number) => postJson<unknown>(`/api/deadletters/${id}/replay`)
};