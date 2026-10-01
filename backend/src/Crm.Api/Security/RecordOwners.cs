using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Security;

public static class RecordOwners
{
    /// <summary>Owner of a record of any object (tenant-filtered), or null if it doesn't exist in the caller's tenant.</summary>
    public static async Task<Guid?> OwnerOfAsync(CrmDbContext db, string objectKey, Guid id, CancellationToken ct) => objectKey switch
    {
        ObjectKeys.Account => await db.Accounts.Where(x => x.Id == id).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct),
        ObjectKeys.Contact => await db.Contacts.Where(x => x.Id == id).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct),
        ObjectKeys.Lead => await db.Leads.Where(x => x.Id == id).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct),
        ObjectKeys.Opportunity => await db.Opportunities.Where(x => x.Id == id).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct),
        ObjectKeys.Activity => await db.Activities.Where(x => x.Id == id).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct),
        _ => await db.CustomRecords.Where(x => x.Id == id && x.ObjectDefinition!.Key == objectKey).Select(x => (Guid?)x.OwnerId).SingleOrDefaultAsync(ct),
    };
}
