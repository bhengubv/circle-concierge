import { test, expect } from '@playwright/test';

test('desktop moves between workspace, library and projects', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/');
  await expect(page.locator('.ws-brand-lockup')).toContainText('Concierge');
  await page.goto('/library');
  await expect(page.getByRole('heading', { name: 'Library' })).toBeVisible();
  await expect(page.getByRole('searchbox', { name: 'Search your library' })).toBeVisible();
  await page.goto('/projects');
  await expect(page.getByRole('heading', { name: 'Projects', exact: true })).toBeVisible();
});

test('phone viewport keeps the workspace navigation and settings reachable', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.locator('.ws-brand-lockup')).toContainText('Concierge');
  await page.getByRole('button', { name: 'Open the menu' }).click();
  await expect(page.locator('.ws-side.is-open')).toBeVisible();
  await page.getByRole('button', { name: 'Open AI engine and app settings' }).click();
  await expect(page.getByRole('dialog', { name: 'Settings' })).toBeVisible();
});
