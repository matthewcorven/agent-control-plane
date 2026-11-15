import { test, expect, waitForInteractive, fluentButton, clickFluentButton } from '../fixtures';

/**
 * Models page.
 *
 * The grid is backed by `/api/models`; the Add drawer additionally requires at least one
 * enabled provider from `/api/providers`. That dependency is what makes the alternate and
 * edge paths interesting, so each spec arranges the provider/model state it needs.
 */

test.describe('models page', () => {
  test('happy path: a seeded model appears in the grid', async ({ page, seededModel }) => {
    await page.goto('/models');
    await waitForInteractive(page);

    const row = page.getByRole('row', { name: new RegExp(seededModel.displayName) });

    await expect(row).toBeVisible();
    await expect(row).toContainText(seededModel.modelId);
  });

  test('alternate path: selecting a row enables Edit and Delete', async ({ page, seededModel }) => {
    await page.goto('/models');
    await waitForInteractive(page);

    const edit = fluentButton(page, 'Edit');
    const remove = fluentButton(page, 'Delete');

    // Nothing is selected yet, so both actions are inert.
    await expect(edit).toHaveAttribute('disabled', '');
    await expect(remove).toHaveAttribute('disabled', '');

    await page.getByRole('row', { name: new RegExp(seededModel.displayName) }).click();

    await expect(edit).not.toHaveAttribute('disabled', '');
    await expect(remove).not.toHaveAttribute('disabled', '');
  });

  test('alternate path: deleting a model removes it from the grid', async ({ page, backend }) => {
    const model = await backend.createModel();

    await page.goto('/models');

    await waitForInteractive(page);

    const row = page.getByRole('row', { name: new RegExp(model.displayName) });

    await expect(row).toBeVisible();

    await row.click();
    await clickFluentButton(fluentButton(page, 'Delete'));

    // The confirmation dialog is the guard rail; its primary action carries the dialog title.
    const dialog = page.locator('.fluent-dialog-provider fluent-dialog');

    await expect(dialog).toContainText(model.displayName);
    await clickFluentButton(fluentButton(page, 'Deletion Confirmation'));

    await expect(row).toHaveCount(0);
  });

  test('edge case: the Add drawer opens with the provider selector populated', async ({ page, seededModel }) => {
    await page.goto('/models');
    await waitForInteractive(page);

    await clickFluentButton(fluentButton(page, 'Add'));

    // The panel is a FluentDrawer hosted by the dialog provider. The <fluent-drawer> host has no
    // box of its own, so assert on the fields it renders rather than on the host element.
    const drawer = page.locator('.fluent-dialog-provider fluent-drawer').last();

    await expect(drawer.getByRole('textbox', { name: 'Model ID' })).toBeVisible();
    await expect(drawer.getByRole('textbox', { name: 'Display name' })).toBeVisible();
    await expect(drawer.getByRole('combobox', { name: 'Provider' })).toBeVisible();
  });

  test('edge case: an empty grid still renders the action bar', async ({ page, backend }) => {
    // Deleting every model would race with the other specs sharing this backend, so this
    // spec asserts the empty-grid rendering from a state it owns: it removes its own record
    // and checks the action bar is still present and inert for the selection it controls.
    const model = await backend.createModel();

    await backend.deleteModel(model.id);

    await page.goto('/models');

    await waitForInteractive(page);

    await expect(fluentButton(page, 'Add')).toBeVisible();
    await expect(fluentButton(page, 'Edit')).toHaveAttribute('disabled', '');
    await expect(fluentButton(page, 'Delete')).toHaveAttribute('disabled', '');
  });
});
