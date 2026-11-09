namespace Shirokuro.Application.Observations;

/// <summary>
/// A grant issued by a trusted, future server-side visibility policy.
/// Never bind or deserialize grants directly from an untrusted client request.
/// Granting visibility of an identity does not imply position visibility.
/// No grant can reveal a rival's planned destination.
/// </summary>
public sealed record AuthorizedHeroReveal(string PlayerId, bool RevealPosition);

