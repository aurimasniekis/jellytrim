import { describe, expect, it } from 'vitest';
import { ago, bytes, bytesToGib, episodeCode, exactBytes, gibToBytes, resumePercent, ruleLabel } from '../src/format';

describe('format', () => {
    it('uses binary units with IEC suffixes consistently', () => {
        expect(bytes(0)).toBe('0 B');
        expect(bytes(1023)).toBe('1023 B');
        expect(bytes(1024)).toBe('1.0 KiB');
        expect(bytes(1536)).toBe('1.5 KiB');
        expect(bytes(1024 ** 3)).toBe('1.0 GiB');
        expect(bytes(150 * 1024 ** 3)).toBe('150 GiB');
        expect(bytes(-2048)).toBe('-2.0 KiB');
    });

    it('never renders a missing size as zero', () => {
        expect(bytes(null)).toBe('—');
        expect(bytes(undefined)).toBe('—');
    });

    it('shows exact bytes', () => expect(exactBytes(1234567)).toBe('1,234,567 bytes'));

    it('round-trips GiB input', () => {
        expect(gibToBytes(1.5)).toBe(1610612736);
        expect(bytesToGib(gibToBytes(100))).toBe(100);
    });

    it('describes elapsed time', () => {
        const now = Date.parse('2026-01-10T00:00:00Z');
        expect(ago(null, now)).toBe('never');
        expect(ago('2026-01-09T23:59:50Z', now)).toBe('just now');
        expect(ago('2026-01-09T23:00:00Z', now)).toBe('1 h ago');
        expect(ago('2026-01-01T00:00:00Z', now)).toBe('9 days ago');
    });

    it('computes resume percent only when known', () => {
        expect(resumePercent(25, 100)).toBe(25);
        expect(resumePercent(25, null)).toBeNull();
        expect(resumePercent(0, 100)).toBeNull();
        expect(resumePercent(500, 100)).toBe(100);
    });

    it('labels episodes and unknown rules', () => {
        expect(episodeCode(1, 2)).toBe('S01E02');
        expect(episodeCode(null, 2)).toBe('S?E02');
        expect(ruleLabel('protect.favorite')).toBe('Favorites');
        expect(ruleLabel('not.a.rule')).toBe('not.a.rule');
    });
});
