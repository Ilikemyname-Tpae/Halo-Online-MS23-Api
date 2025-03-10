﻿using Halal_Station_Remastered.Utils.Enums;
using Halal_Station_Remastered.Utils.Requests.UserServices;
using Halal_Station_Remastered.Utils.ResponseUtils;
using Halal_Station_Remastered.Utils.Services.UserServices;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Halal_Station_Remastered.Controllers
{
    [ApiController]
    [Route("UserService.svc")]
    public class UserServices : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public UserServices(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpPost("GetShop")]
        public IActionResult GetShop()
        {
            var shopDirectoryPath = Path.Combine(AppContext.BaseDirectory, "JsonData", "Shop");

            var allShops = Directory.Exists(shopDirectoryPath)
                ? Directory.GetFiles(shopDirectoryPath, "*.json", SearchOption.AllDirectories)
                    .Select(System.IO.File.ReadAllText)
                    .Select(json => JsonSerializer.Deserialize<ShopData>(json))
                    .Where(shop => shop != null)
                    .ToList()
                : new List<ShopData>();

            var response = new
            {
                GetShopResult = new
                {
                    retCode = ClientCodes.Success,
                    data = allShops
                }
            };
            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }

        [HttpPost("GetItemOffers")]
        public IActionResult GetItemOffers()
        {
            var offersDirectoryPath = Path.Combine(AppContext.BaseDirectory, "JsonData", "Offers");

            var allItemOffers = Directory.Exists(offersDirectoryPath)
                ? Directory.GetFiles(offersDirectoryPath, "*.json", SearchOption.AllDirectories)
                    .Select(System.IO.File.ReadAllText)
                    .Select(json => JsonSerializer.Deserialize<ItemOffer>(json))
                    .Where(offer => offer != null)
                    .ToList()
                : new List<ItemOffer>();

            var response = new
            {
                GetItemOffersResult = new
                {
                    retCode = ClientCodes.Success,
                    data = allItemOffers
                }
            };
            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }

        [HttpPost("GetUserStates")]
        public async Task<IActionResult> GetUserStates()
        {
            var userId = Header.ExtractUserIdFromHeaders(Request.Headers);
            var userService = new UserService(_configuration);

            (List<UserState> userStates, string nickname) = await userService.GetUserStatesAsync(userId);

            var response = new
            {
                GetUserStatesResult = new
                {
                    retCode = ClientCodes.Success,
                    data = new
                    {
                        UserStateList = userStates,
                        TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        User = new { Id = userId },
                        Nickname = nickname
                    }
                }
            };
            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }

        [HttpPost("GetUsersBaseData")]
        public async Task<IActionResult> GetUsersBaseData([FromBody] UserBaseDataRequest usersRequest)
        {
            var userIds = usersRequest.Users.Select(u => u.Id).ToList();
            var userService = new UserBaseDataService(_configuration);
            var usersData = await userService.GetUsersBaseDataAsync(userIds);

            var response = new
            {
                GetUsersBaseDataResult = new
                {
                    retCode = ClientCodes.Success,
                    data = usersData
                }
            };
            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }

        [HttpPost("GetTransactionHistory")]
        public async Task<IActionResult> GetTransactionHistory()
        {
            var userId = Header.ExtractUserIdFromHeaders(Request.Headers);
            var transactionService = new TransactionService(_configuration);
            List<object> transactionsList = await transactionService.GetTransactionHistoryAsync(userId);

            var response = new
            {
                GetTransactionHistoryResult = new
                {
                    retCode = ClientCodes.Success,
                    data = new
                    {
                        totalResults = transactionsList.Count,
                        transactions = transactionsList.ToArray()
                    }
                }
            };
            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }

        [HttpPost("GetUsersPrimaryStates")]
        public async Task<IActionResult> GetUsersPrimaryStates([FromBody] GetPrimaryStatesRequest request)
        {
            var primaryStateService = new PrimaryStateService(_configuration);
            var primaryStates = await primaryStateService.GetUsersPrimaryStatesAsync(request.Users);

            var response = new
            {
                GetUsersPrimaryStatesResult = new
                {
                    retCode = ClientCodes.Success,
                    data = primaryStates
                }
            };
            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }

        [HttpPost("GetUsersByNickname")]
        public async Task<IActionResult> GetUsersByNickname([FromBody] GetUsersByNicknameRequest request)
        {
            var userService = new GetUsersByNicknameService(_configuration);
            var users = await userService.GetUsersByNicknameAsync(request.NicknamePrefix, request.MaxResults);

            var response = new
            {
                GetUsersByNicknameResult = new
                {
                    retCode = ClientCodes.Success,
                    data = users
                }
            };
            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }

        [HttpPost("NicknameChange")]
        public async Task<IActionResult> NicknameChange([FromBody] JsonElement requestBody)
        {
            var userId = Header.ExtractUserIdFromHeaders(Request.Headers);

            if (!requestBody.TryGetProperty("nickname", out var nicknameProperty))
            {
                return BadRequest("Nickname is required.");
            }

            var newNickname = nicknameProperty.GetString();


            var userService = new ChangeNicknameService(_configuration);
            bool success = await userService.ChangeNicknameAsync(userId, newNickname);

            if (success)
            {
                var response = new
                {
                    NicknameChangeResult = new
                    {
                        retCode = ClientCodes.Success,
                        data = true
                    }
                };
                return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
            }
            else
            {
                var response = new
                {
                    NicknameChangeResult = new
                    {
                        retCode = ClientCodes.Success,
                        data = false
                    }
                };
                return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
            }
        }

        [HttpPost("ApplyOfferListAndGetTransactionHistory")]
        public async Task<IActionResult> ApplyOfferListAndGetTransactionHistory(
            [FromBody] ApplyOfferListAndGetTransactionHistoryRequest offerRequest)
        {

            var userId = Header.ExtractUserIdFromHeaders(Request.Headers);
            var offerService = new ApplyOfferListAndGetTransactionHistoryService(_configuration);
            var (transactions, errorMessage) = await offerService.ProcessOffersAndGetTransactionsAsync(offerRequest, userId);
            var response = new
            {
                ApplyOfferListAndGetTransactionHistory = new
                {
                    retCode = ClientCodes.Success,
                    data = new
                    {
                        totalResults = transactions.Count,
                        transactions
                    }
                }
            };

            return Header.AddUserContextAndReturnContent(Request.Headers, Response.Headers, response);
        }
    }
}
