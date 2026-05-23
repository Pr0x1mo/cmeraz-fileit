import { useEffect, useMemo, useRef, useState } from "react";
import { api, DeadLetter, Flow, LogRow } from "./api";
import { Pipeline } from "./Pipeline";

const keyOf = (f: Flow) => f.correlationId ?? f.invocationId ?? `id:${f.id}`;
const shortId = (s: string | null | undefined) => (s ? s.slice(0, 8) : "(no id)");

export default function App() {
    const [flows, setFlows] = useState<Flow[]>([]);
    const [dlq, setDlq] = useState<DeadLetter[]>([]);
    const [selected, setSelected] = useState<string | null>(null);
    const [timeline, setTimeline] = useState<LogRow[]>([]);
    const [activeNode, setActiveNode] = useState<string | undefined>(undefined);
    const [busy, setBusy] = useState(false);
    const [toast, setToast] = useState<string | null>(null);

    const refreshLists = async () => {
        try {
            const [f, d] = await Promise.all([api.recentFlows(50), api.deadLetters(50)]);
            setFlows(f);
            setDlq(d);
        } catch (e) {
            console.error(e);
        }
    };

    useEffect(() => {
        refreshLists();
        const id = setInterval(refreshLists, 4000);
        return () => clearInterval(id);
    }, []);

    // Poll the timeline for the selected flow. A freshly triggered flow takes a second or
    // two to land its rows in CommonLog, so we poll for ~20s and stop once rows arrive and
    // stabilize (the pipeline's own replay animation drives the node lighting).
    const pollRef = useRef<number | null>(null);
    useEffect(() => {
        if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
        if (!selected) { setTimeline([]); return; }

        let attempts = 0;
        let lastCount = -1;
        let stableTicks = 0;

        const tick = async () => {
            attempts += 1;
            try {
                const rows = await api.flowTimeline(selected);
                setTimeline(rows);
                if (rows.length === lastCount && rows.length > 0) {
                    stableTicks += 1;
                } else {
                    stableTicks = 0;
                    lastCount = rows.length;
                }
                // Stop once the row count has been stable for 3 ticks or we hit the attempt cap.
                if (stableTicks >= 3 || attempts >= 20) {
                    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
                }
            } catch (e) {
                console.error(e);
            }
        };

        tick();
        pollRef.current = window.setInterval(tick, 1000);
        return () => {
            if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
        };
    }, [selected]);

    const trigger = async (label: string, fn: () => Promise<{ correlationId: string }>) => {
        setBusy(true);
        setToast(`Firing ${label}...`);
        try {
            const r = await fn();
            setActiveNode(undefined);
            setSelected(r.correlationId);
            setToast(`${label} fired. CorrelationId ${r.correlationId}`);
            setTimeout(refreshLists, 1500);
            setTimeout(refreshLists, 5000);
        } catch (e: any) {
            setToast(`${label} failed: ${e?.message ?? e}`);
        } finally {
            setBusy(false);
        }
    };

    const nodeRows = useMemo(() => {
        if (!activeNode) return timeline;
        const filters: Record<string, (r: LogRow) => boolean> = {
            source: r => /source/i.test(r.message ?? ""),
            watcher: r => /Watcher/.test(r.sourceContext ?? ""),
            queue: r => /Queu|Publish|Service Bus|SB|Bus/i.test(r.message ?? ""),
            subscriber: r => /Subscriber/.test(r.sourceContext ?? ""),
            services: r => /Services\.|ApiAdd/.test(r.sourceContext ?? ""),
            complex: r => /Complex|Documents_/.test(r.sourceContext ?? ""),
            final: r => /final/i.test(r.message ?? ""),
            dlq: r => /DeadLetter/.test(r.sourceContext ?? "") || /MaxDelivery|Poison/i.test(r.message ?? "")
        };
        const f = filters[activeNode];
        return f ? timeline.filter(f) : timeline;
    }, [timeline, activeNode]);

    return (
        <div className="h-full w-full grid grid-cols-[280px_1fr_320px] grid-rows-[auto_1fr] gap-2 p-2">
            <header className="col-span-3 flex items-center gap-3 px-3 py-2 bg-slate-900 rounded">
                <div className="text-lg font-semibold whitespace-nowrap">FileIt operator console</div>
                <div className="text-xs text-slate-400 flex-1 text-right whitespace-normal break-words min-w-0">{toast ?? "idle"}</div>
            </header>

            <aside className="row-start-2 bg-slate-900 rounded p-3 flex flex-col gap-3 overflow-auto">
                <div className="text-xs uppercase tracking-wide text-slate-400">Demo triggers</div>
                <button disabled={busy} onClick={() => trigger("Drop CSV", api.dropCsv)} className="rounded bg-emerald-700 hover:bg-emerald-600 disabled:opacity-50 px-3 py-2 text-left">
                    <div className="font-medium">Drop CSV</div>
                    <div className="text-xs text-slate-200/80">dataflow happy path</div>
                </button>
                <button disabled={busy} onClick={() => trigger("Send API", api.sendApi)} className="rounded bg-sky-700 hover:bg-sky-600 disabled:opacity-50 px-3 py-2 text-left">
                    <div className="font-medium">Send API request</div>
                    <div className="text-xs text-slate-200/80">services + complex</div>
                </button>
                <button disabled={busy} onClick={() => trigger("Publish broadcast", api.publishBroadcast)} className="rounded bg-violet-700 hover:bg-violet-600 disabled:opacity-50 px-3 py-2 text-left">
                    <div className="font-medium">Publish broadcast</div>
                    <div className="text-xs text-slate-200/80">simple pub/sub fan-out</div>
                </button>
                <button disabled={busy} onClick={() => trigger("Poison CSV", api.dropPoison)} className="rounded bg-rose-700 hover:bg-rose-600 disabled:opacity-50 px-3 py-2 text-left">
                    <div className="font-medium">Drop poison CSV</div>
                    <div className="text-xs text-slate-200/80">dataflow + DLQ self-heal</div>
                </button>

                <div className="text-xs uppercase tracking-wide text-slate-400 mt-4">Upload your own</div>
                <label className="rounded bg-slate-700 hover:bg-slate-600 px-3 py-2 cursor-pointer text-sm">
                    <input type="file" className="hidden" onChange={async e => {
                        const f = e.target.files?.[0];
                        if (!f) return;
                        setBusy(true);
                        setToast(`Uploading ${f.name}...`);
                        try {
                            const target = /poison/i.test(f.name) ? "dataflow" : (f.name.toLowerCase().endsWith(".csv") ? "dataflow" : "simple");
                            const r = await api.uploadFile(f, target);
                            setActiveNode(undefined);
                            setSelected(r.correlationId);
                            setToast(`Uploaded ${f.name}. CorrelationId ${r.correlationId}`);
                            setTimeout(refreshLists, 1500);
                            setTimeout(refreshLists, 5000);
                        } catch (e: any) {
                            setToast(`Upload failed: ${e?.message ?? e}`);
                        } finally {
                            setBusy(false);
                            e.target.value = "";
                        }
                    }} />
                    Pick file...
                </label>
                <div className="text-[10px] text-slate-500 -mt-2">CSV goes to dataflow. Other files go to simple.</div>

                <div className="text-xs uppercase tracking-wide text-slate-400 mt-4">Recent flows</div>
                <div className="flex flex-col gap-1 overflow-auto">
                    {flows.map(f => {
                        const k = keyOf(f);
                        return (
                            <button key={f.id} onClick={() => { setSelected(k); setActiveNode(undefined); }} className={`text-left text-xs px-2 py-1 rounded hover:bg-slate-800 ${selected === k ? "bg-slate-800" : ""}`}>
                                <div className="font-mono">{shortId(k)}</div>
                                <div className="text-slate-400">{(f.application ?? "").replace("FileIt.Module.", "").replace(".Host", "")} {new Date(f.createdOn).toLocaleTimeString()}</div>
                            </button>
                        );
                    })}
                </div>
            </aside>

            <main className="row-start-2 bg-slate-900 rounded flex flex-col">
                <div className="flex-1 border-b border-slate-800 min-h-0">
                    <Pipeline timeline={timeline} onPickNode={setActiveNode} activeNode={activeNode} />
                </div>
                <div className="overflow-auto p-2 text-xs font-mono resize-y min-h-[120px] max-h-[70vh]" style={{ height: 240 }}>
                    <div className="px-2 py-1 text-slate-400 uppercase tracking-wide break-all whitespace-normal">
                        {selected ? `Timeline for ${selected} ${activeNode ? `(filtered to ${activeNode})` : ""}` : "Pick a flow"}
                    </div>
                    {nodeRows.map(r => (
                        <div key={r.id} className="px-2 py-0.5 hover:bg-slate-800 rounded flex gap-2">
                            <span className="text-slate-500 whitespace-nowrap">{new Date(r.createdOn).toLocaleTimeString()}</span>
                            <span className="text-cyan-300 whitespace-nowrap">{r.eventName ?? "(step)"}</span>
                            <span className="text-slate-400 truncate">{(r.application ?? "").replace("FileIt.Module.", "").replace(".Host", "")}</span>
                        </div>
                    ))}
                </div>
            </main>

            <aside className="row-start-2 bg-slate-900 rounded p-3 overflow-auto">
                <div className="text-xs uppercase tracking-wide text-slate-400 mb-2">Dead-letter inbox</div>
                {dlq.length === 0 && <div className="text-xs text-slate-500">No dead-lettered records</div>}
                {dlq.map(d => (
                    <div key={d.deadLetterRecordId} className="border border-slate-800 rounded p-2 mb-2 text-xs">
                        <div className="flex items-center justify-between">
                            <span className="font-mono">#{d.deadLetterRecordId}</span>
                            <span className="text-rose-400">{d.failureCategory}</span>
                        </div>
                        <div className="text-slate-400">{d.sourceEntityName}</div>
                        <div className="text-slate-500">delivery {d.deliveryCount}, {d.status}</div>
                        <button onClick={() => api.replay(d.deadLetterRecordId).then(() => setToast(`Replay queued for #${d.deadLetterRecordId}`)).catch(e => setToast(`Replay failed: ${e?.message}`))} className="mt-1 px-2 py-0.5 rounded bg-emerald-800 hover:bg-emerald-700">Replay</button>
                    </div>
                ))}
            </aside>
        </div>
    );
}