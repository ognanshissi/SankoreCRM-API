using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <summary>
    /// Hides « Montant souhaité » in the lead layouts already saved, and takes it off their creation
    /// form (product owner, 2026-11-02): a saved layout keeps its own flags, so the catalogue's new
    /// default reaches only the tenants that never saved one. A tenant that made the field required
    /// keeps it — a required field is always visible and on the creation form. The version is not
    /// bumped: nothing else moves, and an editor already open would otherwise be refused at its next
    /// save.
    /// </summary>
    public partial class DesiredAmountOffLeadQuickAdd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE administration.entity_form_layouts AS l
                SET layout = jsonb_set(l.layout, '{fields}', (
                    SELECT jsonb_agg(
                               CASE WHEN f->>'key' = 'desiredAmount'
                                         AND COALESCE((f->>'isRequired')::boolean, false) = false
                                    THEN f || '{"quickAdd": false, "isHidden": true}'::jsonb
                                    ELSE f
                               END
                               ORDER BY t.ord)
                    FROM jsonb_array_elements(l.layout->'fields') WITH ORDINALITY AS t(f, ord)))
                WHERE l.entity = 'Lead'
                  AND l.layout->'fields' @> '[{"key": "desiredAmount"}]'::jsonb
                  AND NOT l.layout->'fields' @> '[{"key": "desiredAmount", "isRequired": true}]'::jsonb;
                """);
        }

        /// <inheritdoc />
        /// <remarks>Nothing to undo with certainty: an administrator may have chosen either value since.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
