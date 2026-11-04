namespace Chatbot.Domain;

public enum ParticipantStatus { Invited, Active, Revoked, Deleted }

public enum ConsentType { PrivacyNotice, ExplicitConsent, ResearchUse }

public enum PeplauPhase { Orientation, Identification, Exploitation, Resolution }

public enum MessageRole { User, Assistant }

/// <summary>How an assistant message ended. Null for user messages.</summary>
public enum MessageCompletion { Complete, Stopped, Incomplete }

public enum ActorType { Participant, Researcher, System, Anonymous }
