import express, { Router, type Express } from 'express';
import { problem } from './core/errors.js';
import { authenticate, errorHandler, rateLimit, securityHeaders } from './http/pipeline.js';
import { coreRoutes } from './routes/core.js';

/**
 * The SupermarketBilling API on Firebase: the same paths, bodies, cookies and problem answers as the .NET API it
 * replaces, so the web apps work unchanged (they forward /api and /health to API_INTERNAL_URL).
 */
export function createApp(): Express {
  const app = express();
  app.disable('x-powered-by');
  app.set('trust proxy', true);
  app.use(securityHeaders);
  app.use(express.json({ limit: '4mb' }));
  app.use(authenticate);
  app.use(rateLimit('global'));

  const router = Router();
  coreRoutes(router);
  app.use(router);

  app.use((_req, res) => {
    res.status(404).json(problem(404, 'not_found', 'Not found.'));
  });
  app.use(errorHandler);
  return app;
}
