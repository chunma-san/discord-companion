# Monitoring without conversational AI

This public reference supports optional host-status commands. It does not include a general-purpose AI chat room, conversation polling, persistent conversation memory or chat web search.

In customized deployments, keep automatic monitoring independent of conversational features:

- Collect health observations on a schedule, with timestamped reports.
- Send alerts only for persistent failures, meaningful state changes and recovery.
- Keep notification delivery and conventional health checks working when an optional analysis provider is unavailable.
- Give any analysis component a fixed read-only snapshot, not a shell, arbitrary file access or the Docker socket.
- Remove chat background-service registrations and conversational command definitions when the chat feature is retired. Rebuild and restart the bot, then verify its Discord command registrations.
- Delete retired chat configuration and conversation journals from active storage. Provider-hosted records, Discord retention and historical backups have their own retention policies; removing an active journal does not establish erasure from those systems.
- Keep credentials, channel identifiers, network addresses and operational state in private deployment configuration.

Automatic analysis is an optional deployment extension, not an implemented feature of this public snapshot. Do not replace customized deployments with this reference repository.
