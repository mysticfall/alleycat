<!-- event-history: speech.observed -->
{% if ActorId != blank %}{% if ActorId == character.FullId %}I said: {{ Content }}{% else %}Heard {{ ActorId }} say: {{ Content }}{% endif %}{% else %}Heard an unknown speaker say: {{ Content }}{% endif %}{% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}
<!-- event-history: fallback -->
((Received {{ TypeKey }} event.)){% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}
