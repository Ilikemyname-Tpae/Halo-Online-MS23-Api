﻿using Halal_Station_Remastered.Utils.Requests.UserServices;
using MySql.Data.MySqlClient;
using System.Text.Json;

namespace Halal_Station_Remastered.Utils.Services.UserServices
{
    public class ApplyOfferListAndGetTransactionHistoryService
    {
        private readonly string _connectionString;

        public ApplyOfferListAndGetTransactionHistoryService(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public async Task<(List<object> transactions, string errorMessage)> ProcessOffersAndGetTransactionsAsync(
            ApplyOfferListAndGetTransactionHistoryRequest offerRequest, int? userId)
        {
            if (!userId.HasValue)
                return (new List<object>(), "Invalid user ID");

            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();
            using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var transactions = new List<object>();
                foreach (var offerId in offerRequest.OfferIds)
                {
                    var (offer, offerError) = await GetOfferDetailsAsync(connection, transaction, offerId);
                    if (offerError != null)
                        return (new List<object>(), offerError);

                    var currencyType = offerId.EndsWith("_cr") ? "Credits" : "Gold";
                    var initialValue = await GetUserCurrencyAsync(connection, transaction, userId.Value, currencyType);

                    if (initialValue < offer.Price)
                        return (new List<object>(), $"Insufficient {currencyType}");

                    var resultingValue = initialValue - offer.Price;
                    var transactionEntry = CreateTransactionEntry(offerId, initialValue, resultingValue, currencyType, offerRequest.HistoryFromTime);

                    await RecordTransactionAsync(connection, transaction, userId.Value, offerId, initialValue, resultingValue, offer.Price, transactionEntry);
                    await UpdateUserCurrencyAsync(connection, transaction, userId.Value, currencyType, resultingValue);

                    if (IsSpecialOffer(offerId))
                    {
                        await HandleSpecialOfferAsync(connection, transaction, userId.Value, offerId);
                    }

                    transactions.Add(transactionEntry);
                }

                await transaction.CommitAsync();
                return (transactions, null);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return (new List<object>(), ex.Message);
            }
        }
        private async Task<(Offer offer, string error)> GetOfferDetailsAsync(MySqlConnection connection, MySqlTransaction transaction, string offerId)
        {
            var offersDirectoryPath = Path.Combine(AppContext.BaseDirectory, "JsonData", "Offers");
            var allItemOffers = new List<ItemOffer>();

            if (Directory.Exists(offersDirectoryPath))
            {
                var jsonFiles = Directory.GetFiles(offersDirectoryPath, "*.json", SearchOption.AllDirectories);
                foreach (var filePath in jsonFiles)
                {
                    var jsonContent = File.ReadAllText(filePath);
                    var itemOffer = JsonSerializer.Deserialize<ItemOffer>(jsonContent);
                    if (itemOffer != null)
                    {
                        allItemOffers.Add(itemOffer);
                    }
                }
            }

            var offer = allItemOffers
                .SelectMany(io => io.OfferLine.SelectMany(ol => ol.Offers))
                .FirstOrDefault(o => o.OfferId == offerId);

            if (offer == null)
            {
                return (null, $"Offer not found: {offerId}");
            }

            return (offer, null);
        }

        private async Task<int> GetUserCurrencyAsync(MySqlConnection connection, MySqlTransaction transaction, int userId, string currencyType)
        {
            const string query = @"SELECT Value FROM userstates WHERE UserId = @UserId AND StateName = @CurrencyType";
            using (var command = new MySqlCommand(query, connection, transaction))
            {
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@CurrencyType", currencyType);

                var result = await command.ExecuteScalarAsync();
                return result != null && int.TryParse(result.ToString(), out var value) ? value : 0;
            }
        }

        private async Task UpdateUserCurrencyAsync(MySqlConnection connection, MySqlTransaction transaction, int userId, string currencyType, int newValue)
        {
            const string query = @"UPDATE userstates SET Value = @NewValue WHERE UserId = @UserId AND StateName = @CurrencyType";

            using (var command = new MySqlCommand(query, connection, transaction))
            {
                command.Parameters.AddWithValue("@NewValue", newValue);
                command.Parameters.AddWithValue("@UserId", userId);
                command.Parameters.AddWithValue("@CurrencyType", currencyType);

                await command.ExecuteNonQueryAsync();
            }
        }
        private async Task RecordTransactionAsync(MySqlConnection connection, MySqlTransaction transaction,
     int userId, string offerId, int initialValue, int resultingValue, int price, dynamic transactionEntry)
        {
            var duration = await GetOfferDurationAsync(offerId);
            bool isLoadout = offerId.StartsWith("weapon_loadout") || offerId.StartsWith("armor_loadout");

            const string query = @"
INSERT INTO transactions 
(UserId, OfferId, InitialValue, ResultingValue, DeltaValue, OperationType, 
 SessionId, ReferenceId, TimeStamp, StateName, StateType, OwnType, DescId)
VALUES 
(@UserId, @OfferId, @InitialValue, @ResultingValue, @DeltaValue, @OperationType,
 @SessionId, @ReferenceId, @TimeStamp, @StateName, @StateType, @OwnType, @DescId)";

            using var cmd = new MySqlCommand(query, connection, transaction);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@OfferId", offerId);

            if (isLoadout)
            {
                cmd.Parameters.AddWithValue("@InitialValue", 0);
                cmd.Parameters.AddWithValue("@ResultingValue", 1);
                cmd.Parameters.AddWithValue("@DeltaValue", 1);
                cmd.Parameters.AddWithValue("@StateType", 0);
                cmd.Parameters.AddWithValue("@OwnType", 1);
            }
            else
            {
                cmd.Parameters.AddWithValue("@InitialValue", duration);
                cmd.Parameters.AddWithValue("@ResultingValue", duration);
                cmd.Parameters.AddWithValue("@DeltaValue", 0);
                cmd.Parameters.AddWithValue("@StateType", 4);
                cmd.Parameters.AddWithValue("@OwnType", 2);
            }

            cmd.Parameters.AddWithValue("@OperationType", 0);
            cmd.Parameters.AddWithValue("@SessionId", transactionEntry.sessionId);
            cmd.Parameters.AddWithValue("@ReferenceId", transactionEntry.referenceId);
            cmd.Parameters.AddWithValue("@TimeStamp", transactionEntry.timeStamp);
            cmd.Parameters.AddWithValue("@StateName", offerId.Replace("_cr", ""));
            cmd.Parameters.AddWithValue("@DescId", 0);

            await cmd.ExecuteNonQueryAsync();

            if (isLoadout)
            {
                await AddToUserStatesAsync(connection, transaction, userId, offerId);
            }
        }
        private async Task AddToUserStatesAsync(MySqlConnection connection, MySqlTransaction transaction, int userId, string stateName)
        {
            const string checkQuery = @"SELECT COUNT(*) FROM userstates WHERE UserId = @UserId AND StateName = @StateName";
            using (var checkCmd = new MySqlCommand(checkQuery, connection, transaction))
            {
                checkCmd.Parameters.AddWithValue("@UserId", userId);
                checkCmd.Parameters.AddWithValue("@StateName", stateName);

                var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

                if (count > 0)
                {
                    const string updateQuery = @"
                UPDATE userstates 
                SET Value = 1, OwnType = 1, StateType = 0
                WHERE UserId = @UserId AND StateName = @StateName";

                    using var updateCmd = new MySqlCommand(updateQuery, connection, transaction);
                    updateCmd.Parameters.AddWithValue("@UserId", userId);
                    updateCmd.Parameters.AddWithValue("@StateName", stateName);
                    await updateCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    const string insertQuery = @"
                INSERT INTO userstates (UserId, StateName, Value, OwnType, StateType)
                VALUES (@UserId, @StateName, 1, 1, 0)";

                    using var insertCmd = new MySqlCommand(insertQuery, connection, transaction);
                    insertCmd.Parameters.AddWithValue("@UserId", userId);
                    insertCmd.Parameters.AddWithValue("@StateName", stateName);
                    await insertCmd.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task<int> GetOfferDurationAsync(string offerId)
        {
            var offersDirectoryPath = Path.Combine(AppContext.BaseDirectory, "JsonData", "Offers");
            var allItemOffers = new List<ItemOffer>();

            if (Directory.Exists(offersDirectoryPath))
            {
                var jsonFiles = Directory.GetFiles(offersDirectoryPath, "*.json", SearchOption.AllDirectories);
                foreach (var filePath in jsonFiles)
                {
                    var jsonContent = await File.ReadAllTextAsync(filePath);
                    var itemOffer = JsonSerializer.Deserialize<ItemOffer>(jsonContent);
                    if (itemOffer != null)
                    {
                        foreach (var offerLine in itemOffer.OfferLine)
                        {
                            var offer = offerLine.Offers.FirstOrDefault(o => o.OfferId == offerId);
                            if (offer != null)
                            {
                                return offerLine.Duration;
                            }
                        }
                    }
                }
            }
            return 0;
        }

        private object CreateTransactionEntry(string offerId, int initialValue, int resultingValue, string currencyType, long timestamp)
        {
            var transactionItems = new List<object>();

            if (offerId.StartsWith("weapon_loadout") || offerId.StartsWith("armor_loadout"))
            {
                transactionItems.Add(new
                {
                    stateName = offerId,
                    stateType = 0,
                    ownType = 1,
                    operationType = 0,
                    initialValue = 0,
                    resultingValue = 1,
                    deltaValue = 1,
                    descId = 0
                });

                transactionItems.Add(new
                {
                    stateName = currencyType.ToLower(),
                    stateType = currencyType == "Credits" ? 2 : 3,
                    ownType = 0,
                    operationType = 0,
                    initialValue = initialValue,
                    resultingValue = resultingValue,
                    deltaValue = initialValue - resultingValue,
                    descId = 0
                });
            }
            else
            {
                transactionItems.Add(new
                {
                    stateName = offerId.Replace("_cr", ""),
                    stateType = 4,
                    ownType = 2,
                    operationType = 0,
                    initialValue = GetOfferDurationAsync(offerId).Result,
                    resultingValue = GetOfferDurationAsync(offerId).Result,
                    deltaValue = 0,
                    descId = 2
                });

                transactionItems.Add(new
                {
                    stateName = currencyType.ToLower(),
                    stateType = currencyType == "Credits" ? 2 : 3,
                    ownType = 0,
                    operationType = 0,
                    initialValue = initialValue,
                    resultingValue = resultingValue,
                    deltaValue = initialValue - resultingValue,
                    descId = 0
                });
            }

            return new
            {
                transactionItems = transactionItems,
                sessionId = Guid.NewGuid().ToString(),
                referenceId = Guid.NewGuid().ToString(),
                offerId = offerId,
                timeStamp = timestamp,
                operationType = 0,
                extendedInfoItems = new[] { new { Key = "", Value = "" } }
            };
        }

        private async Task HandleSpecialOfferAsync(MySqlConnection connection, MySqlTransaction transaction, int userId, string offerId)
        {
            if (IsKitOffer(offerId))
            {
                const string query = @"
                UPDATE userstates 
                SET Value = 0, OwnType = 0 
                WHERE UserId = @UserId AND StateName = 'class_select_token';";

                using var cmd = new MySqlCommand(query, connection, transaction);
                cmd.Parameters.AddWithValue("@UserId", userId);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private bool IsSpecialOffer(string offerId)
        {
            return IsKitOffer(offerId) || offerId.StartsWith("challenge");
        }

        private bool IsKitOffer(string offerId)
        {
            return offerId == "ranger_kit_offer" ||
                   offerId == "sniper_kit_offer" ||
                   offerId == "tactician_kit_offer";
        }
    }
}