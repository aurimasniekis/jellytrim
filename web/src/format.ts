import type { RuleOutcome, WatchState } from './types';

const UNITS = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'];
const GIB = 1024 ** 3;

/** One convention everywhere: binary units, shown with the IEC suffix so that they are never mistaken for GB. */
export function bytes(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    let scaled = Math.abs(value);
    let unit = 0;
    while (scaled >= 1024 && unit < UNITS.length - 1) {
        scaled /= 1024;
        unit++;
    }

    return `${value < 0 ? '-' : ''}${unit === 0 || scaled >= 100 ? scaled.toFixed(0) : scaled.toFixed(1)} ${UNITS[unit]}`;
}

export const exactBytes = (value: number) => `${value.toLocaleString('en-US')} bytes`;

export const gibToBytes = (gib: number) => Math.round(gib * GIB);

export const bytesToGib = (value: number) => Number((value / GIB).toFixed(2));

/** Absolute local time with its zone, so that a timestamp is never ambiguous. */
export function dateTime(iso: string | null | undefined): string {
    if (!iso) return '—';
    return new Date(iso).toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', timeZoneName: 'short' });
}

export function date(iso: string | null | undefined): string {
    if (!iso) return '—';
    return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

export function ago(iso: string | null | undefined, now = Date.now()): string {
    if (!iso) return 'never';
    const minutes = Math.round((now - new Date(iso).getTime()) / 60000);
    if (minutes < 1) return 'just now';
    if (minutes < 60) return `${minutes} min ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 48) return `${hours} h ago`;
    return `${Math.round(hours / 24)} days ago`;
}

export const daysAgoIso = (days: number, now = Date.now()) => new Date(now - days * 86400000).toISOString();

export const WATCH_LABEL: Record<WatchState, string> = { Played: 'Played', Partial: 'In progress', Unplayed: 'Unplayed', Unknown: 'Unknown' };

export const OUTCOME_LABEL: Record<RuleOutcome, string> = { Pass: 'Allows', Block: 'Blocks', Unknown: 'Unknown, blocks', NotApplicable: 'Not applicable' };

export const RULE_LABEL: Record<string, string> = {
    'health.available': 'Files available',
    'protect.activePlayback': 'Not playing now',
    'protect.explicit': 'Explicit protection',
    'protect.favorite': 'Favorites',
    'protect.inProgress': 'In progress',
    'protect.keepRecentlyAdded': 'Keep recently added',
    'protect.keepNewestEpisodes': 'Keep newest episodes',
    'protect.excludedLibrary': 'Excluded library',
    'protect.excludedPath': 'Excluded path',
    'protect.excludedTag': 'Excluded tag',
    'scope.library': 'Library in scope',
    'scope.mediaKind': 'Media type in scope',
    'users.configured': 'Users selected',
    'watched.all': 'Played by all selected',
    'watched.any': 'Played by any selected',
    'lastWatched.age': 'Last played age',
    'added.age': 'Age since added',
    'size.min': 'Minimum size',
    'unit.complete': 'Complete season or show',
};

export const ruleLabel = (id: string) => RULE_LABEL[id] ?? id;

export function episodeCode(season: number | null, episode: number | null): string {
    if (season === null && episode === null) return '';
    const pad = (n: number | null) => (n === null ? '?' : String(n).padStart(2, '0'));
    return `S${pad(season)}E${pad(episode)}`;
}

export function resumePercent(positionTicks: number, runtimeTicks: number | null): number | null {
    if (!runtimeTicks || positionTicks <= 0) return null;
    return Math.min(100, Math.round((positionTicks / runtimeTicks) * 100));
}
