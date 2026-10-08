using Microsoft.Data.SqlClient;
using System;

namespace TradingApp.Business.Services.Helpers
{
    public static class SqlExceptionClassifier
    {
        public static bool IsUniqueConstraintOrUniqueIndexViolation(Exception exception)
        {
            // 2627 — violation of a unique constraint (UQ_ConversationChunks_ConversationId_Key)
            // 2601 — violation of a unique index
            return exception.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601);
        }
    }
}
