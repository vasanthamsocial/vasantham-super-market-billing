import type { NextConfig } from 'next';

/** The counter agent on the counter PC itself (D-018). */
export declare const counterAgentOrigin: string;

/** @param options.connectTo extra origins the pages may call (Content-Security-Policy connect-src). */
export declare function createNextConfig(options: { appName: string; connectTo?: string[] }): NextConfig;
