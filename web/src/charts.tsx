import { useRef, useState } from 'preact/hooks';
import { bytes, dateTime, exactBytes } from './format';
import type { SnapshotPoint } from './types';

/** Fixed categorical order, validated with the dark and light chart surfaces. Identity follows the scope, not its rank. */
const SERIES_COUNT = 8;
export const seriesVar = (index: number) => `var(--jt-series-${(index % SERIES_COUNT) + 1})`;

type Segment = { label: string; value: number; count?: number; color: string };

export function StackedBar({ segments, label }: { segments: Segment[]; label: string }) {
    const total = segments.reduce((sum, s) => sum + s.value, 0);
    const [tip, setTip] = useState<Segment | null>(null);
    if (total === 0) return <div class="jt-muted jt-small">No data.</div>;
    const text = segments.map(s => `${s.label} ${bytes(s.value)}`).join(', ');
    return (
        <div class="jt-stack-wrap">
            <div class="jt-stack" role="img" aria-label={`${label}: ${text}`} onMouseLeave={() => setTip(null)}>
                {segments.filter(s => s.value > 0).map(s => (
                    <div key={s.label} class="jt-stack-seg" style={{ flexGrow: s.value, background: s.color }} onMouseEnter={() => setTip(s)} />
                ))}
            </div>
            <div class="jt-small jt-stack-tip" aria-hidden="true">
                {tip ? <span><strong>{tip.label}</strong>: {bytes(tip.value)}{tip.count !== undefined && ` across ${tip.count} item(s)`} ({Math.round((tip.value / total) * 100)}%)</span> : ' '}
            </div>
        </div>
    );
}

export function Legend({ items }: { items: { label: string; color: string; value?: string }[] }) {
    return (
        <ul class="jt-legend">
            {items.map(i => <li key={i.label}><span class="jt-swatch" style={{ background: i.color }} aria-hidden="true" />{i.label}{i.value && <span class="jt-muted"> {i.value}</span>}</li>)}
        </ul>
    );
}

export function Meter({ used, total, tone, label }: { used: number; total: number; tone: 'good' | 'warning' | 'critical'; label: string }) {
    const pct = total > 0 ? Math.min(100, (used / total) * 100) : 0;
    return (
        <div class="jt-meter" role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(pct)}>
            <div class={`jt-meter-fill jt-meter-${tone}`} style={{ width: `${pct}%` }} />
        </div>
    );
}

type Series = { id: string; name: string; color: string; points: { t: number; v: number }[] };

/** One measure on one axis. Different measures get separate charts, never a second scale. */
export function TrendChart({ series, label, unitLabel }: { series: Series[]; label: string; unitLabel: string }) {
    const [hover, setHover] = useState<number | null>(null);
    const svg = useRef<SVGSVGElement>(null);
    const width = 540;
    const height = 200;
    const pad = { left: 62, right: series.length <= 4 ? 96 : 12, top: 12, bottom: 26 };
    const times = Array.from(new Set(series.flatMap(s => s.points.map(p => p.t)))).sort((a, b) => a - b);
    const max = Math.max(1, ...series.flatMap(s => s.points.map(p => p.v)));
    const min = times[0];
    const span = Math.max(1, times[times.length - 1] - min);
    const x = (t: number) => (times.length === 1 ? (pad.left + width - pad.right) / 2 : pad.left + ((t - min) / span) * (width - pad.left - pad.right));
    const y = (v: number) => height - pad.bottom - (v / max) * (height - pad.top - pad.bottom);
    const ticks = [0, 0.5, 1].map(f => f * max);
    const onMove = (e: MouseEvent) => {
        const rect = svg.current!.getBoundingClientRect();
        const at = ((e.clientX - rect.left) / rect.width) * width;
        let best = times[0];
        for (const t of times) if (Math.abs(x(t) - at) < Math.abs(x(best) - at)) best = t;
        setHover(best);
    };
    const nearest = (s: Series, t: number) => s.points.find(p => p.t === t);
    const hoverLeft = hover === null ? 0 : (x(hover) / width) * 100;
    return (
        <div class="jt-trend">
            <svg ref={svg} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label} onMouseMove={onMove} onMouseLeave={() => setHover(null)}>
                {ticks.map(t => (
                    <g key={t}>
                        <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} class="jt-grid" />
                        <text x={pad.left - 8} y={y(t) + 4} text-anchor="end" class="jt-axis">{bytes(t)}</text>
                    </g>
                ))}
                <text x={pad.left} y={height - 8} class="jt-axis">{new Date(min).toLocaleDateString()}</text>
                {times.length > 1 && <text x={width - pad.right} y={height - 8} text-anchor="end" class="jt-axis">{new Date(times[times.length - 1]).toLocaleDateString()}</text>}
                {series.map(s => (
                    <g key={s.id}>
                        {s.points.length > 1 && <polyline fill="none" stroke={s.color} stroke-width="2" stroke-linejoin="round" stroke-linecap="round" points={s.points.map(p => `${x(p.t)},${y(p.v)}`).join(' ')} />}
                        {s.points.length === 1 && <circle cx={x(s.points[0].t)} cy={y(s.points[0].v)} r="4" fill={s.color} />}
                        {series.length <= 4 && s.points.length > 0 && <text x={width - pad.right + 8} y={y(s.points[s.points.length - 1].v) + 4} class="jt-axis jt-axis-strong">{s.name.slice(0, 13)}</text>}
                    </g>
                ))}
                {hover !== null && <line x1={x(hover)} x2={x(hover)} y1={pad.top} y2={height - pad.bottom} class="jt-crosshair" />}
                {hover !== null && series.map(s => { const p = nearest(s, hover); return p ? <circle key={s.id} cx={x(p.t)} cy={y(p.v)} r="4" fill={s.color} class="jt-dot-ring" /> : null; })}
            </svg>
            {hover !== null && (
                <div class="jt-tooltip" style={{ left: `${Math.min(hoverLeft, 72)}%` }}>
                    <div class="jt-small jt-muted">{dateTime(new Date(hover).toISOString())}</div>
                    {series.map(s => { const p = nearest(s, hover); return p ? <div key={s.id} class="jt-small"><span class="jt-swatch" style={{ background: s.color }} />{s.name}: <strong>{bytes(p.v)}</strong></div> : null; })}
                </div>
            )}
            {series.length >= 2 && <Legend items={series.map(s => ({ label: s.name, color: s.color }))} />}
            <details class="jt-small">
                <summary>View as table</summary>
                <div class="jt-table-wrap"><table class="jt-table"><thead><tr><th scope="col">Time</th>{series.map(s => <th key={s.id} scope="col">{s.name} ({unitLabel})</th>)}</tr></thead>
                    <tbody>{times.slice(-30).map(t => <tr key={t}><td>{dateTime(new Date(t).toISOString())}</td>{series.map(s => { const p = nearest(s, t); return <td key={s.id} title={p ? exactBytes(p.v) : undefined}>{p ? bytes(p.v) : '—'}</td>; })}</tr>)}</tbody></table></div>
            </details>
        </div>
    );
}

export function toSeries(points: SnapshotPoint[], value: (p: SnapshotPoint) => number): Series[] {
    const groups = new Map<string, Series>();
    const order = Array.from(new Set(points.map(p => p.scopeId))).sort();
    for (const p of points) {
        let series = groups.get(p.scopeId);
        if (!series) {
            series = { id: p.scopeId, name: p.scopeName, color: seriesVar(order.indexOf(p.scopeId)), points: [] };
            groups.set(p.scopeId, series);
        }

        series.points.push({ t: new Date(p.takenUtc).getTime(), v: value(p) });
    }

    return Array.from(groups.values());
}
