import type { ComponentChildren } from 'preact';
import { useCallback, useEffect, useId, useRef, useState } from 'preact/hooks';
import { ApiError } from './api';
import { bytes, exactBytes } from './format';

export function useAsync<T>(load: () => Promise<T>, deps: unknown[]) {
    const [data, setData] = useState<T | null>(null);
    const [error, setError] = useState<ApiError | Error | null>(null);
    const [loading, setLoading] = useState(true);
    const ticket = useRef(0);
    const reload = useCallback(async () => {
        const mine = ++ticket.current;
        setLoading(true);
        try {
            const value = await load();
            if (mine === ticket.current) {
                setData(value);
                setError(null);
            }
        } catch (e) {
            if (mine === ticket.current) setError(e as Error);
        } finally {
            if (mine === ticket.current) setLoading(false);
        }
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, deps);
    useEffect(() => { void reload(); }, [reload]);
    return { data, error, loading, reload, setData };
}

/** Polls while mounted, and pauses in hidden tabs. */
export function usePoll(callback: () => void, intervalMs: number | null) {
    const saved = useRef(callback);
    saved.current = callback;
    useEffect(() => {
        if (intervalMs === null) return undefined;
        const id = setInterval(() => { if (!document.hidden) saved.current(); }, intervalMs);
        return () => clearInterval(id);
    }, [intervalMs]);
}

export function Size({ value }: { value: number | null | undefined }) {
    if (value === null || value === undefined) return <span>—</span>;
    return <span class="jt-num" title={exactBytes(value)}>{bytes(value)}</span>;
}

export function Loading({ label = 'Loading' }: { label?: string }) {
    return <div class="jt-state" role="status" aria-live="polite"><span class="jt-spinner" aria-hidden="true" />{label}…</div>;
}

export function Empty({ title, children }: { title: string; children?: ComponentChildren }) {
    return <div class="jt-state"><strong>{title}</strong>{children && <div class="jt-muted">{children}</div>}</div>;
}

export function ErrorState({ error, onRetry }: { error: Error; onRetry?: () => void }) {
    const api = error instanceof ApiError ? error : null;
    const title = api?.isPermission ? 'Access denied' : api?.isOffline ? 'Server unreachable' : 'Something went wrong';
    return (
        <div class="jt-alert jt-alert-error" role="alert">
            <strong>{title}</strong>
            <div>{error.message}</div>
            {api?.correlationId && <div class="jt-muted jt-small">Reference: <code>{api.correlationId}</code></div>}
            {onRetry && !api?.isPermission && <div><Button onClick={onRetry}>Try again</Button></div>}
        </div>
    );
}

export function Alert({ kind, title, children }: { kind: 'info' | 'warning' | 'error' | 'good'; title?: string; children: ComponentChildren }) {
    const icon = { info: 'i', warning: '!', error: '✕', good: '✓' }[kind];
    return (
        <div class={`jt-alert jt-alert-${kind}`} role={kind === 'error' ? 'alert' : 'status'}>
            <span class="jt-alert-icon" aria-hidden="true">{icon}</span>
            <div>{title && <strong>{title} </strong>}{children}</div>
        </div>
    );
}

type ButtonProps = { onClick?: () => void; children: ComponentChildren; variant?: 'primary' | 'danger' | 'plain'; disabled?: boolean; why?: string | null; type?: 'button' | 'submit'; label?: string; small?: boolean };

/** A disabled action states why in text, reachable by screen readers, not in a hover-only tooltip. */
export function Button({ onClick, children, variant, disabled, why, type = 'button', label, small }: ButtonProps) {
    const id = useId();
    const blocked = disabled || Boolean(why);
    return (
        <span class="jt-btn-wrap">
            <button type={type} class={`jt-btn ${variant ? `jt-btn-${variant}` : ''} ${small ? 'jt-btn-small' : ''}`} onClick={onClick} disabled={blocked} aria-label={label} aria-describedby={why ? id : undefined}>{children}</button>
            {why && <span id={id} class="jt-why">{why}</span>}
        </span>
    );
}

export function Badge({ tone, children, title }: { tone?: 'good' | 'warning' | 'serious' | 'critical' | 'info' | 'neutral'; children: ComponentChildren; title?: string }) {
    return <span class={`jt-badge jt-badge-${tone ?? 'neutral'}`} title={title}>{children}</span>;
}

export function Card({ title, children, actions, note }: { title?: string; children: ComponentChildren; actions?: ComponentChildren; note?: ComponentChildren }) {
    return (
        <section class="jt-card">
            {(title || actions) && <header class="jt-card-head"><div><h2>{title}</h2>{note && <p class="jt-muted jt-small">{note}</p>}</div>{actions && <div class="jt-card-actions">{actions}</div>}</header>}
            {children}
        </section>
    );
}

export function Stat({ label, value, sub, title }: { label: string; value: ComponentChildren; sub?: ComponentChildren; title?: string }) {
    return <div class="jt-stat" title={title}><div class="jt-stat-label">{label}</div><div class="jt-stat-value">{value}</div>{sub && <div class="jt-muted jt-small">{sub}</div>}</div>;
}

export function Pager({ offset, limit, total, onChange, label }: { offset: number; limit: number; total: number; onChange: (offset: number) => void; label: string }) {
    if (total === 0) return null;
    const end = Math.min(offset + limit, total);
    return (
        <nav class="jt-pager" aria-label={label}>
            <span class="jt-muted">{(offset + 1).toLocaleString()}–{end.toLocaleString()} of {total.toLocaleString()}</span>
            <Button small disabled={offset === 0} onClick={() => onChange(Math.max(0, offset - limit))}>Previous</Button>
            <Button small disabled={end >= total} onClick={() => onChange(offset + limit)}>Next</Button>
        </nav>
    );
}

/** Modal dialog: traps Tab, closes on Escape, and returns focus to whatever opened it. */
export function Dialog({ title, onClose, children, wide, side }: { title: string; onClose: () => void; children: ComponentChildren; wide?: boolean; side?: boolean }) {
    const ref = useRef<HTMLDivElement>(null);
    const titleId = useId();
    useEffect(() => {
        const previous = document.activeElement as HTMLElement | null;
        const root = ref.current!;
        (root.querySelector<HTMLElement>('[data-autofocus]') ?? root).focus();
        return () => previous?.focus?.();
    }, []);
    const onKeyDown = (e: KeyboardEvent) => {
        if (e.key === 'Escape') {
            e.stopPropagation();
            onClose();
        } else if (e.key === 'Tab') {
            const focusable = Array.from(ref.current!.querySelectorAll<HTMLElement>('a[href],button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),summary,[tabindex="0"]'));
            if (focusable.length === 0) return;
            const first = focusable[0];
            const last = focusable[focusable.length - 1];
            if (e.shiftKey && document.activeElement === first) {
                e.preventDefault();
                last.focus();
            } else if (!e.shiftKey && document.activeElement === last) {
                e.preventDefault();
                first.focus();
            }
        }
    };
    return (
        <div class={`jt-overlay ${side ? 'jt-overlay-side' : ''}`} onMouseDown={e => { if (e.target === e.currentTarget) onClose(); }}>
            <div ref={ref} class={`jt-dialog ${wide ? 'jt-dialog-wide' : ''}`} role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1} onKeyDown={onKeyDown}>
                <header class="jt-dialog-head">
                    <h2 id={titleId}>{title}</h2>
                    <button type="button" class="jt-icon-btn" aria-label="Close" onClick={onClose}>✕</button>
                </header>
                <div class="jt-dialog-body">{children}</div>
            </div>
        </div>
    );
}

export function Field({ label, children, hint }: { label: string; children: ComponentChildren; hint?: string }) {
    return <label class="jt-field"><span class="jt-field-label">{label}</span>{children}{hint && <span class="jt-muted jt-small">{hint}</span>}</label>;
}

export function NumberInput({ value, onChange, step, min, ...rest }: { value: number | null; onChange: (v: number | null) => void; step?: string; min?: number; placeholder?: string; 'aria-label'?: string }) {
    return <input class="jt-input" type="number" inputMode="decimal" value={value ?? ''} step={step} min={min} onInput={e => { const raw = (e.target as HTMLInputElement).value; onChange(raw === '' ? null : Number(raw)); }} {...rest} />;
}

export function CheckboxList({ legend, options, selected, onChange }: { legend: string; options: { value: string; label: string; note?: string }[]; selected: string[]; onChange: (next: string[]) => void }) {
    return (
        <fieldset class="jt-checks">
            <legend>{legend}</legend>
            {options.length === 0 && <span class="jt-muted">None available.</span>}
            {options.map(o => (
                <label key={o.value} class="jt-check">
                    <input type="checkbox" checked={selected.includes(o.value)} onChange={e => onChange((e.target as HTMLInputElement).checked ? [...selected, o.value] : selected.filter(v => v !== o.value))} />
                    <span>{o.label}{o.note && <span class="jt-muted jt-small"> ({o.note})</span>}</span>
                </label>
            ))}
        </fieldset>
    );
}

/** A compact multi-select that opens in place. Clicking or focusing outside it, or pressing Escape, closes it. */
export function MultiPick({ label, options, selected, onChange }: { label: string; options: { value: string; label: string; note?: string }[]; selected: string[]; onChange: (next: string[]) => void }) {
    const ref = useRef<HTMLDetailsElement>(null);
    useEffect(() => {
        const outside = (e: Event) => {
            const el = ref.current;
            if (el?.open && e.target instanceof Node && !el.contains(e.target)) el.open = false;
        };
        const escape = (e: KeyboardEvent) => {
            const el = ref.current;
            if (e.key === 'Escape' && el?.open) {
                el.open = false;
                el.querySelector('summary')?.focus();
            }
        };
        document.addEventListener('pointerdown', outside);
        document.addEventListener('focusin', outside);
        document.addEventListener('keydown', escape);
        return () => {
            document.removeEventListener('pointerdown', outside);
            document.removeEventListener('focusin', outside);
            document.removeEventListener('keydown', escape);
        };
    }, []);
    return (
        <details class="jt-pick" ref={ref}>
            <summary class="jt-input">{label}{selected.length > 0 ? `: ${selected.length}` : ': all'}</summary>
            <div class="jt-pick-menu"><CheckboxList legend={label} options={options} selected={selected} onChange={onChange} /></div>
        </details>
    );
}
