import type { NextConfig } from 'next';
import { counterAgentOrigin, createNextConfig } from '@sb/web-shared/next-config';

// The billing counters also talk to the counter agent on their own PC (printer, drawer, scale, display).
const config: NextConfig = createNextConfig({ appName: 'Billing and Operations', connectTo: [counterAgentOrigin] });

export default config;
