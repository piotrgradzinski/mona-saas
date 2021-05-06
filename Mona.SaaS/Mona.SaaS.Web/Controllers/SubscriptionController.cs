﻿// MICROSOFT CONFIDENTIAL INFORMATION
//
// Copyright © Microsoft Corporation
//
// Microsoft Corporation (or based on where you live, one of its affiliates) licenses this preview code for your internal testing purposes only.
//
// Microsoft provides the following preview code AS IS without warranty of any kind. The preview code is not supported under any Microsoft standard support program or services.
//
// Microsoft further disclaims all implied warranties including, without limitation, any implied warranties of merchantability or of fitness for a particular purpose. The entire risk arising out of the use or performance of the preview code remains with you.
//
// In no event shall Microsoft be liable for any damages whatsoever (including, without limitation, damages for loss of business profits, business interruption, loss of business information, or other pecuniary loss) arising out of the use of or inability to use the preview code, even if Microsoft has been advised of the possibility of such damages.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mona.SaaS.Core.Constants;
using Mona.SaaS.Core.Enumerations;
using Mona.SaaS.Core.Extensions;
using Mona.SaaS.Core.Interfaces;
using Mona.SaaS.Core.Models;
using Mona.SaaS.Core.Models.Configuration;
using Mona.SaaS.Core.Models.Events;
using Mona.SaaS.EventProcessing.Interfaces;
using Mona.SaaS.Web.Extensions;
using Mona.SaaS.Web.Models;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Mona.SaaS.Web.Controllers
{
    public class SubscriptionController : Controller
    {
        public static class ErrorCodes
        {
            public const string UnableToResolveMarketplaceToken = "UnableToResolveMarketplaceToken";
            public const string SubscriptionNotFound = "SubscriptionNotFound";
            public const string SubscriptionActivationFailed = "SubscriptionActivationFailed";
        }

        private readonly DeploymentConfiguration deploymentConfig;
        private readonly OfferConfiguration offerConfig;

        private readonly ILogger logger;
        private readonly IMarketplaceOperationService mpOperationService;
        private readonly IMarketplaceSubscriptionService mpSubscriptionService;
        private readonly ISubscriptionEventPublisher subscriptionEventPublisher;
        private readonly ISubscriptionRepository subscriptionRepo;

        public SubscriptionController(
            IOptionsSnapshot<DeploymentConfiguration> deploymentConfig,
            OfferConfiguration offerConfig,
            ILogger<SubscriptionController> logger,
            IMarketplaceOperationService mpOperationService,
            IMarketplaceSubscriptionService mpSubscriptionService,
            ISubscriptionEventPublisher subscriptionEventPublisher,
            ISubscriptionRepository subscriptionRepo)
        {
            this.deploymentConfig = deploymentConfig.Value;
            this.logger = logger;
            this.offerConfig = offerConfig;
            this.mpOperationService = mpOperationService;
            this.mpSubscriptionService = mpSubscriptionService;
            this.subscriptionEventPublisher = subscriptionEventPublisher;
            this.subscriptionRepo = subscriptionRepo;
        }

        [Authorize]
        [HttpPost]
        [Route("/", Name = "landing")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> PostLiveLandingPageAsync(LandingPageModel landingPageModel) => PostLandingPageAsync(landingPageModel);

        [Authorize(Policy = "admin")]
        [HttpPost]
        [Route("/test", Name = "landing/test")]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> PostTestLandingPageAsync(LandingPageModel landingPageModel)
        {
            if (this.deploymentConfig.IsTestModeEnabled)
            {
                return PostLandingPageAsync(landingPageModel, inTestMode: true);
            }
            else
            {
                return Task.FromResult(NotFound() as IActionResult); // Test mode is disabled...
            }
        }

        [AllowAnonymous]
        [HttpGet]
        [Route("/", Name = "landing")]
        public Task<IActionResult> GetLiveLandingPageAsync(string token = null) => GetLandingPageAsync(token);

        [Authorize(Policy = "admin")]
        [HttpGet]
        [Route("/test", Name = "landing/test")]
        public Task<IActionResult> GetTestLandingPageAsync()
        {
            if (this.deploymentConfig.IsTestModeEnabled)
            {
                return GetLandingPageAsync(inTestMode: true);
            }
            else
            {
                return Task.FromResult(NotFound() as IActionResult); // Test mode is disabled...
            }
        }

        [AllowAnonymous]
        [HttpPost]
        [Route("/webhook", Name = "webhook")]
        public Task<IActionResult> ProcessLiveWebhookNotificationAsync([FromBody] WebhookNotification whNotification) => ProcessWebhookNotificationAsync(whNotification);

        [AllowAnonymous]
        [HttpPost]
        [Route("/webhook/test", Name = "webhook/test")]
        public Task<IActionResult> ProcessTestWebhookNotificationAsync([FromBody] WebhookNotification whNotification)
        {
            if (this.deploymentConfig.IsTestModeEnabled)
            {
                return ProcessWebhookNotificationAsync(whNotification, inTestMode: true);
            }
            else
            {
                return Task.FromResult(NotFound() as IActionResult); // Test mode is disabled...
            }
        }

        private async Task<IActionResult> PostLandingPageAsync(LandingPageModel landingPageModel, bool inTestMode = false)
        {
            try
            {
                var subscription = await TryGetSubscriptionAsync(landingPageModel.SubscriptionId, inTestMode);

                if (subscription == null)
                {
                    // Well, this is awkward. We've presumably sent the user to the subscription landing page which means that we
                    // were indeed able to resolve the subscription but, for some reason, we don't know about it. Let the user know
                    // and prompt them to return to the AppSource/Marketplace offer URL...

                    this.logger.LogError($"Subscription [{subscription.SubscriptionId}] not found.");

                    return View("Index", new LandingPageModel(inTestMode)
                        .WithCurrentUserInformation(User)
                        .WithOfferInformation(this.offerConfig)
                        .WithErrorCode(ErrorCodes.SubscriptionNotFound));
                }
                else
                {
                    // Alright, we're done here. Redirect the user to their subscription...

                    await this.subscriptionEventPublisher.PublishEventAsync(new SubscriptionPurchased(subscription));

                    var subPurchasedUrl = this.offerConfig.SubscriptionPurchaseConfirmationUrl.WithSubscriptionId(subscription.SubscriptionId);

                    this.logger.LogInformation($"Subscription [{subscription.SubscriptionId}] purchase confirmed. Redirecting user to [{subPurchasedUrl}]...");

                    return Redirect(subPurchasedUrl);
                }
            }
            catch (Exception ex)
            {
                // Uh oh. Something broke. Log it and let the user know...

                this.logger.LogError(ex,
                    $"An error occurred while try to complete subscription [{landingPageModel.SubscriptionId}] activation. " +
                    $"See inner exception for details.");

                return View("Index", new LandingPageModel(inTestMode)
                    .WithCurrentUserInformation(User)
                    .WithOfferInformation(this.offerConfig)
                    .WithErrorCode(ErrorCodes.SubscriptionActivationFailed));
            }
        }

        private async Task<IActionResult> GetLandingPageAsync(string token = null, bool inTestMode = false)
        {
            if (this.offerConfig.IsSetupComplete == false)
            {
                // Not so fast... you need to complete the setup wizard before you can access the landing page.           
                // TODO: Need to think about what happens if a non-admin user accesses the landing page but Mona has not yet been set up. Just return a 404 I guess? Feels kind of clunky...

                return RedirectToRoute("setup");
            }

            if (string.IsNullOrEmpty(token) && !inTestMode)
            {
                // We don't have a token so we aren't coming from the AppSource/Marketplace. Try to redirect to service marketing page...

                this.logger.LogWarning("Landing page reached but no subscription token was provided. Attempting to redirect to service marketing page...");

                return TryToRedirectToServiceMarketingPageUrl();
            }
            else
            {
                // We have a token (or we're in test mode) so we're almost certainly coming from the AppSource/Marketplace...

                if (User.Identity.IsAuthenticated)
                {
                    // The default landing page experience...

                    var subscription = await TryResolveSubscriptionTokenAsync(token, inTestMode);

                    if (subscription == null)
                    {
                        // The Marketplace can't resolve the provided token so we need to kick back an error
                        // to the user and point them back to the original AppSource/Marketplace listing...

                        this.logger.LogWarning($"Unable to resolve source subscription token [{token}].");

                        return View("Index", new LandingPageModel(inTestMode)
                            .WithCurrentUserInformation(User)
                            .WithOfferInformation(this.offerConfig)
                            .WithErrorCode(ErrorCodes.UnableToResolveMarketplaceToken));
                    }
                    else
                    {
                        // TODO: Just a thought... at this point, we can assume that the user has purchased a subscription through the AppSource/Marketplace but
                        // what if they don't confirm and this is the farthest that they ever get? If that's the case, I'd think that the publisher would want to
                        // be aware of this "almost purchase." Does it make sense to fire a [SubscriptionPurchasing] event here so that this scenario can be tracked
                        // and possibly followed up on by the publisher's sales team? Did something happen that made the user reconsider their purchase?

                        if (subscription.Status == SubscriptionStatus.PendingActivation)
                        {
                            // Score! New customer! Let's get them over to the landing page so they can complete their purchase and
                            // we can get their subscription spun up...

                            this.logger.LogInformation(
                                $"Subscription [{subscription.SubscriptionId}] is unknown to Mona. " +
                                $"Presenting user with default subscription purchase confirmation page...");

                            if (inTestMode) 
                            {
                                await this.subscriptionRepo.PutSubscriptionAsync(subscription).ConfigureAwait(false);
                            }
                               
                            return View("Index", new LandingPageModel(inTestMode)
                                .WithCurrentUserInformation(User)
                                .WithOfferInformation(this.offerConfig)
                                .WithSubscriptionInformation(subscription));
                        }
                        else
                        {
                            // We already know about this subscription. Redirecting to publisher-defined subscription configuration UI...

                            var subConfigUrl = this.offerConfig.SubscriptionConfigurationUrl.WithSubscriptionId(subscription.SubscriptionId);

                            this.logger.LogInformation(
                                $"Subscription [{subscription.SubscriptionId}] is known to Mona. " +
                                $"Redirecting user to subscription configuration page at [{subConfigUrl}]...");

                            return Redirect(subConfigUrl);
                        }
                    }
                }
                else
                {
                    // User needs to authenticate first...

                    this.logger.LogWarning($"User has provided a subscription token [{token}] but has not yet been authenticated. Challenging...");

                    return Challenge();
                }
            }
        }

        private async Task<IActionResult> ProcessWebhookNotificationAsync(WebhookNotification whNotification, bool inTestMode = false)
        {
            try
            {
                var subscription = await TryGetSubscriptionAsync(whNotification.SubscriptionId, inTestMode);

                if (subscription == null)
                {
                    // We don't even know about this subscription...

                    this.logger.LogError(
                        $"Unable to process Marketplace webhook notification [{whNotification.OperationId}]. " +
                        $"Subscription [{whNotification.SubscriptionId}] not found.");

                    return NotFound();
                }
                else
                {
                    // Let's look at the action type and decide how to handle it...

                    var opType = ToCoreOperationType(whNotification.ActionType);

                    this.logger.LogInformation($"Processing subscription [{subscription.SubscriptionId}] webhook [{opType}] operation [{whNotification.OperationId}]...");

                    await VerifyWebhookNotificationAsync(whNotification, inTestMode);

                    switch (opType)
                    {
                        case SubscriptionOperationType.Cancel:
                            await this.subscriptionEventPublisher.PublishEventAsync(
                                new SubscriptionCancelled(subscription, whNotification.OperationId));
                            break;
                        case SubscriptionOperationType.ChangePlan:
                            await this.subscriptionEventPublisher.PublishEventAsync(
                                new SubscriptionPlanChanged(subscription, whNotification.OperationId, whNotification.PlanId));
                            break;
                        case SubscriptionOperationType.ChangeSeatQuantity:
                            await this.subscriptionEventPublisher.PublishEventAsync(
                                new SubscriptionSeatQuantityChanged(subscription, whNotification.OperationId, whNotification.SeatQuantity));
                            break;
                        case SubscriptionOperationType.Reinstate:
                            await this.subscriptionEventPublisher.PublishEventAsync(
                                new SubscriptionReinstated(subscription, whNotification.OperationId));
                            break;
                        case SubscriptionOperationType.Suspend:
                            await this.subscriptionEventPublisher.PublishEventAsync(
                                new SubscriptionSuspended(subscription, whNotification.OperationId));
                            break;
                    }

                    if (inTestMode)
                    {
                        await this.subscriptionRepo.PutSubscriptionAsync(subscription);
                    }

                    this.logger.LogInformation($"Subscription [{subscription.SubscriptionId}] webhook [{opType}] operation [{whNotification.OperationId}] processed successfully.");

                    return Ok();
                }
            }
            catch (Exception ex)
            {
                // Uh oh... something else broke. Log it and let the Marketplace know. If it's important, hopefully they'll call us back...

                this.logger.LogError(ex,
                    $"An error occurred while trying to process Marketplace webhook notification [{whNotification.OperationId}]. " +
                    $"See inner exception for details.");

                return StatusCode((int)(HttpStatusCode.InternalServerError));
            }
        }

        private IActionResult TryToRedirectToServiceMarketingPageUrl() =>
            string.IsNullOrEmpty(this.offerConfig.OfferMarketingPageUrl) ? NotFound() as IActionResult : Redirect(this.offerConfig.OfferMarketingPageUrl);

        private async Task<Subscription> TryGetSubscriptionAsync(string subscriptionId, bool inTestMode = false)
        {
            try
            {
                if (inTestMode)
                {
                    // We're in test mode.
                    // Try to pull the "mock" subscription from local (blob storage by default) cache...

                    this.logger.LogWarning($"[TEST MODE]: Trying to get test subscription [{subscriptionId}] from subscription cache...");

                    return await this.subscriptionRepo.GetSubscriptionAsync(subscriptionId);
                }
                else
                {
                    // Try to get the "record of truth" subscription information from the Marketplace...

                    return await this.mpSubscriptionService.GetSubscriptionAsync(subscriptionId);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, $"An error occurred while trying to get subscription [{subscriptionId}]. See inner exception for details.");

                throw;
            }
        }

        private async Task<Subscription> TryResolveSubscriptionTokenAsync(string subscriptionToken, bool inTestMode = false)
        {
            try
            {
                if (inTestMode)
                {
                    // We're in test mode so there's no actual subscription to resolve.
                    // Instead, we'll create a "mock" subscription.

                    return CreateTestSubscription();
                }
                else
                {
                    // Try to get the subscription information from the Marketplace...

                    return await this.mpSubscriptionService.ResolveSubscriptionTokenAsync(subscriptionToken);
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, $"An error occurred while attempting to resolve subscription token [{subscriptionToken}]. See exception for details.");

                return null;
            }
        }

        private async Task VerifyWebhookNotificationAsync(WebhookNotification whNotification, bool inTestMode = false)
        {
            if (inTestMode)
            {
                this.logger.LogWarning(
                    $"[TEST MODE]: Verifying subscription [{whNotification.SubscriptionId}] operation " +
                    $"[{whNotification.OperationId}] in test mode. Bypassing Marketplace API and continuing...");
            }
            else
            {
                var operation = await this.mpOperationService.GetSubscriptionOperationAsync(
                    whNotification.SubscriptionId, whNotification.OperationId);

                if (operation == null ||
                    operation.OperationId != whNotification.OperationId ||
                    operation.OperationType != ToCoreOperationType(whNotification.ActionType) ||
                    operation.SubscriptionId != whNotification.SubscriptionId)
                {
                    throw new ApplicationException($"Unable to verify subscription [{whNotification.SubscriptionId}] operation [{whNotification.OperationId}].");
                }
            }
        }

        private SubscriptionOperationType ToCoreOperationType(string mpActionType)
        {
            return mpActionType switch
            {
                MarketplaceActionTypes.ChangePlan => SubscriptionOperationType.ChangePlan,
                MarketplaceActionTypes.ChangeQuantity => SubscriptionOperationType.ChangeSeatQuantity,
                MarketplaceActionTypes.Reinstate => SubscriptionOperationType.Reinstate,
                MarketplaceActionTypes.Suspend => SubscriptionOperationType.Suspend,
                MarketplaceActionTypes.Unsubscribe => SubscriptionOperationType.Cancel,
                _ => throw new ArgumentException($"Action type [{mpActionType}] unknown.")
            };
        }

        private Subscription CreateTestSubscription() => new Subscription
        {
            SubscriptionId = TryGetQueryStringParameter("subscriptionId", Guid.NewGuid().ToString()),
            SubscriptionName = TryGetQueryStringParameter("subscriptionName", "Test Subscription"),
            OfferId = TryGetQueryStringParameter("offerId", "Test Offer"),
            PlanId = TryGetQueryStringParameter("planId", "Test Plan"),
            IsTest = true,
            IsFreeTrial = TryParseBooleanQueryStringParameter("isFreeTrial", false).Value,
            SeatQuantity = TryParseIntQueryStringParameter("seatQuantity"),
            Term = CreateTestMarketplaceTerm(),
            Beneficiary = CreateTestMarketplaceUser("beneficiary", "beneficiary@microsoft.com"),
            Purchaser = CreateTestMarketplaceUser("purchaser", "purchaser@microsoft.com"),
            Status = SubscriptionStatus.PendingActivation
        };

        private MarketplaceTerm CreateTestMarketplaceTerm() => new MarketplaceTerm
        {
            EndDate = TryParseDateTimeQueryStringParameter("term_endDate", DateTime.UtcNow.Date.AddMonths(1)),
            StartDate = TryParseDateTimeQueryStringParameter("term_startDate", DateTime.UtcNow.Date),
            TermUnit = TryGetQueryStringParameter("term_termUnit", "PT1M")
        };

        private MarketplaceUser CreateTestMarketplaceUser(string keyPrefix, string defaultUserEmail) => new MarketplaceUser
        {
            AadObjectId = TryGetQueryStringParameter($"{keyPrefix}_aadObjectId", Guid.NewGuid().ToString()),
            AadTenantId = TryGetQueryStringParameter($"{keyPrefix}_aadTenantId", Guid.NewGuid().ToString()),
            UserEmail = TryGetQueryStringParameter($"{keyPrefix}_userEmail", defaultUserEmail),
            UserId = TryGetQueryStringParameter($"{keyPrefix}_userId", Guid.NewGuid().ToString())
        };

        private string TryGetQueryStringParameter(string key, string defaultValue = null) =>
            (Request.Query.TryGetValue(key, out var value) ? value.ToString() : defaultValue);

        private bool? TryParseBooleanQueryStringParameter(string key, bool? defaultValue = null) =>
            (Request.Query.TryGetValue(key, out var value) ? bool.Parse(value.ToString()) : defaultValue);

        private DateTime? TryParseDateTimeQueryStringParameter(string key, DateTime? defaultValue = null) =>
            (Request.Query.TryGetValue(key, out var value) ? DateTime.Parse(value.ToString()) : defaultValue);

        private int? TryParseIntQueryStringParameter(string key, int? defaultValue = null) =>
            (Request.Query.TryGetValue(key, out var value) ? int.Parse(value.ToString()) : defaultValue);
    }
}