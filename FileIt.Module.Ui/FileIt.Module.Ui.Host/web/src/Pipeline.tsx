import { useEffect, useMemo, useState } from "react";
import ReactFlow, { Background, Controls, MarkerType, Node, Edge, Handle, Position } from "reactflow";
import "reactflow/dist/style.css";
import type { LogRow } from "./api";

type Props = {
    timeline: LogRow[];
    onPickNode: (nodeId: string) => void;
    activeNode?: string;
};

// Plain-language explanation shown as a callout above each box when it lights up.
// Written for a non-technical audience watching the demo.
const TEACH: Record<string, string> = {
    source: "A file just landed here. Think of this like a mail inbox tray that the system is watching.",
    watcher: "A robot noticed the new file, wrote down that it arrived, and moved it to a work area so nothing else grabs it twice.",
    queue: "It dropped a ticket into a line (a queue). Work waits politely in line here, so nothing gets lost even if the system is busy.",
    subscriber: "A worker pulled the next ticket from the line and started doing the real job: reading the file and crunching the data.",
    services: "A request came in asking the system to do something. The system said 'got it' instantly, then did the work in the background.",
    complex: "To finish the job, it called another service, like a kitchen passing your order to the grill station that specializes in it.",
    final: "Done. The cleaned, summarized result was filed in the 'finished' pile and saved to the database.",
    dlq: "This file kept failing. Instead of crashing everything, the system set it aside in a holding pen for a human, and it can be retried later."
};

function nodeForRow(r: LogRow): string | null {
    const e = r.eventName ?? "";
    const ctx = r.sourceContext ?? "";
    if (/DeadLetter|MaxDelivery|Poison/.test(e) || /DeadLetter/.test(ctx)) return "dlq";
    if (/MoveToFinal|Completed/.test(e)) return "final";
    if (/ApiAdd|Services/.test(e) || /Services\./.test(ctx)) return "services";
    if (/Complex|Documents_/.test(e) || /Complex/.test(ctx)) return "complex";
    if (/Subscriber|Transform|BlobToolGetFile/.test(e) || /Subscriber/.test(ctx)) return "subscriber";
    if (/BusTool|QueueTransform|Enqueued|SendMessage/.test(e)) return "queue";
    if (/Watcher|MoveToWorking|AddRequestLog|BlobToolMove/.test(e) || /Watcher/.test(ctx)) return "watcher";
    return null;
}

// Custom node: a labelled box with an optional teaching callout above it.
function TeachNode({ data }: { data: any }) {
    const { label, hit, dlq, active, teach } = data;
    const box: React.CSSProperties = {
        borderRadius: 6,
        padding: "8px 12px",
        fontSize: 13,
        width: 150,
        textAlign: "center",
        color: hit ? "#fff" : "#cbd5e1",
        background: hit ? (dlq ? "rgba(225,29,72,0.85)" : "rgba(16,185,129,0.85)") : "rgba(51,65,85,0.6)",
        border: `1px solid ${hit ? (dlq ? "#fda4af" : "#6ee7b7") : "#64748b"}`,
        boxShadow: active ? "0 0 0 4px #fcd34d" : "none",
        transition: "all 200ms ease"
    };
    return (
        <div style={{ position: "relative" }}>
            <Handle type="target" position={Position.Left} style={{ opacity: 0 }} />
            {hit && teach && (
                <div style={{
                    position: "absolute",
                    bottom: "calc(100% + 10px)",
                    left: "50%",
                    transform: "translateX(-50%)",
                    width: 200,
                    background: dlq ? "rgba(76,5,25,0.97)" : "rgba(2,44,34,0.97)",
                    border: `1px solid ${dlq ? "#fda4af" : "#6ee7b7"}`,
                    color: "#e2e8f0",
                    fontSize: 11,
                    lineHeight: 1.35,
                    borderRadius: 8,
                    padding: "8px 10px",
                    textAlign: "left",
                    zIndex: 10,
                    boxShadow: "0 6px 20px rgba(0,0,0,0.5)"
                }}>
                    {teach}
                    <div style={{
                        position: "absolute",
                        top: "100%",
                        left: "50%",
                        transform: "translateX(-50%)",
                        width: 0,
                        height: 0,
                        borderLeft: "7px solid transparent",
                        borderRight: "7px solid transparent",
                        borderTop: `7px solid ${dlq ? "#fda4af" : "#6ee7b7"}`
                    }} />
                </div>
            )}
            <div style={box}>{label}</div>
            <Handle type="source" position={Position.Right} style={{ opacity: 0 }} />
        </div>
    );
}

const nodeTypes = { teach: TeachNode };

export function Pipeline({ timeline, onPickNode, activeNode }: Props) {
    const [cursor, setCursor] = useState(0);

    const ordered = useMemo(
        () => [...timeline].sort((a, b) => +new Date(a.createdOn) - +new Date(b.createdOn)),
        [timeline]
    );

    useEffect(() => {
        if (ordered.length === 0) { setCursor(0); return; }
        setCursor(0);
        let i = 0;
        const id = setInterval(() => {
            i += 1;
            setCursor(i);
            if (i >= ordered.length) clearInterval(id);
        }, 500);
        return () => clearInterval(id);
    }, [ordered]);

    const reached = useMemo(() => {
        const set = new Set<string>();
        for (let i = 0; i < Math.min(cursor, ordered.length); i++) {
            const n = nodeForRow(ordered[i]);
            if (n) set.add(n);
        }
        return set;
    }, [cursor, ordered]);

    const dlqHit = reached.has("dlq");

    const mk = (id: string, label: string, x: number, y: number, hit: boolean): Node => ({
        id,
        type: "teach",
        position: { x, y },
        data: { label, hit, dlq: id === "dlq", active: id === activeNode, teach: TEACH[id] },
        draggable: false
    });

    const nodes: Node[] = useMemo(() => [
        mk("source", "source blob", 0, 180, reached.has("watcher")),
        mk("watcher", "Watcher", 230, 180, reached.has("watcher")),
        mk("queue", "SB queue", 460, 180, reached.has("queue")),
        mk("subscriber", "Subscriber", 690, 180, reached.has("subscriber")),
        mk("services", "services.ApiAdd", 920, 60, reached.has("services")),
        mk("complex", "complex.Documents", 1150, 60, reached.has("complex")),
        mk("final", "final blob / DB", 920, 300, reached.has("final") && !dlqHit),
        mk("dlq", "DLQ", 690, 400, dlqHit)
    ], [reached, activeNode, dlqHit]);

    const edges: Edge[] = useMemo(() => [
        { id: "e1", source: "source", target: "watcher", animated: reached.has("watcher"), markerEnd: { type: MarkerType.ArrowClosed } },
        { id: "e2", source: "watcher", target: "queue", animated: reached.has("queue"), markerEnd: { type: MarkerType.ArrowClosed } },
        { id: "e3", source: "queue", target: "subscriber", animated: reached.has("subscriber"), markerEnd: { type: MarkerType.ArrowClosed } },
        { id: "e4", source: "subscriber", target: "services", animated: reached.has("services"), markerEnd: { type: MarkerType.ArrowClosed } },
        { id: "e5", source: "services", target: "complex", animated: reached.has("complex"), markerEnd: { type: MarkerType.ArrowClosed } },
        { id: "e6", source: "subscriber", target: "final", animated: reached.has("final") && !dlqHit, markerEnd: { type: MarkerType.ArrowClosed } },
        { id: "e7", source: "subscriber", target: "dlq", animated: dlqHit, style: { stroke: "#f87171" }, markerEnd: { type: MarkerType.ArrowClosed, color: "#f87171" } }
    ], [reached, dlqHit]);

    return (
        <div className="h-full w-full">
            <ReactFlow
                nodes={nodes}
                edges={edges}
                nodeTypes={nodeTypes}
                onNodeClick={(_, n) => onPickNode(n.id)}
                defaultViewport={{ x: 40, y: 30, zoom: 0.62 }}
                minZoom={0.2}
                maxZoom={1.5}
                nodesDraggable={false}
                proOptions={{ hideAttribution: true }}
            >
                <Background gap={16} color="#1e293b" />
                <Controls showInteractive={false} />
            </ReactFlow>
        </div>
    );
}