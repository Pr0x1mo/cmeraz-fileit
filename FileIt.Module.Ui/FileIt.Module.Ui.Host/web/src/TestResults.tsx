import { useEffect, useState } from "react";

type Test = { name: string; className: string; outcome: string; duration: string };
type Project = { project: string; total: number; passed: number; failed: number; tests: Test[] };
type Snapshot = {
    generatedUtc: string;
    total: number; passed: number; failed: number; skipped: number;
    projects: Project[];
};

export function TestResults() {
    const [data, setData] = useState<Snapshot | null>(null);
    const [err, setErr] = useState<string | null>(null);
    const [open, setOpen] = useState<Record<string, boolean>>({});

    useEffect(() => {
        fetch("/api/ui/test-results.json", { cache: "no-store" })
            .then(r => r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`)))
            .then(setData)
            .catch(e => setErr(e.message));
    }, []);

    if (err) return <div className="text-xs text-rose-400">Test results not loaded: {err}</div>;
    if (!data) return <div className="text-xs text-slate-500">Loading test results...</div>;

    const generated = new Date(data.generatedUtc).toLocaleString();

    return (
        <div className="text-xs">
            <div className="text-xs uppercase tracking-wide text-slate-400 mb-2">Test results</div>
            <div className="border border-slate-800 rounded p-2 mb-3">
                <div className="flex justify-between">
                    <span className="text-slate-300 font-medium">{data.total} total</span>
                    <span className="text-emerald-400">{data.passed} passed</span>
                </div>
                <div className="flex justify-between mt-0.5">
                    <span className={data.failed > 0 ? "text-rose-400" : "text-slate-500"}>{data.failed} failed</span>
                    <span className="text-slate-500">{data.skipped} skipped</span>
                </div>
                <div className="text-[10px] text-slate-500 mt-1">snapshot {generated}</div>
            </div>

            {data.projects.filter(p => p.total > 0).map(p => {
                const isOpen = !!open[p.project];
                const allGreen = p.failed === 0;
                return (
                    <div key={p.project} className="border border-slate-800 rounded mb-2 overflow-hidden">
                        <button
                            onClick={() => setOpen(o => ({ ...o, [p.project]: !o[p.project] }))}
                            className="w-full px-2 py-1 flex items-center justify-between hover:bg-slate-800"
                        >
                            <span className="truncate text-slate-300">{p.project.replace("FileIt.", "")}</span>
                            <span className={`ml-2 whitespace-nowrap ${allGreen ? "text-emerald-400" : "text-rose-400"}`}>
                                {p.passed}/{p.total}
                            </span>
                        </button>
                        {isOpen && (
                            <div className="px-2 py-1 bg-slate-950/40 max-h-64 overflow-auto">
                                {p.tests.map((t, i) => (
                                    <div key={i} className="flex gap-2 py-0.5">
                                        <span className={t.outcome === "Passed" ? "text-emerald-400" : "text-rose-400"}>
                                            {t.outcome === "Passed" ? "PASS" : "FAIL"}
                                        </span>
                                        <span className="text-slate-300 truncate">{t.name}</span>
                                    </div>
                                ))}
                            </div>
                        )}
                    </div>
                );
            })}
        </div>
    );
}