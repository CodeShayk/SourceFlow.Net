// WASM-08: the bundle's size budget (plan 10 WP-S.7): at most 6 MB brotli-compressed.
import { readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { frameworkDir, frameworkUrl } from './support.ts';

const budget = 6 * 1024 * 1024;

describe('WASM-08 size budget', () => {
  it('ships at most 6 MB brotli, without the core package', () => {
    frameworkUrl(); // fails clearly when the bundle wasn't built
    const compressed = readdirSync(frameworkDir).filter((f) => f.endsWith('.br'));
    expect(compressed.length).toBeGreaterThan(0);
    const total = compressed.reduce((sum, f) => sum + statSync(join(frameworkDir, f)).size, 0);
    console.info(`Studio core bundle: ${(total / 1024 / 1024).toFixed(2)} MB brotli in ${compressed.length} files`);
    expect(total).toBeLessThanOrEqual(budget);
    expect(compressed.some((f) => f.startsWith('SourceFlow.Studio.Wasm'))).toBe(true);
    expect(compressed.some((f) => f.startsWith('SourceFlow.Net') || f.startsWith('Microsoft.Extensions'))).toBe(false);
  });
});
