<!-- event-history: speech.observed -->
{% if ActorId != blank %}{% if ActorId == character.FullId %}I said: {{ Content }}{% else %}Heard {{ ActorId }} say: {{ Content }}{% endif %}{% else %}Heard an unknown speaker say: {{ Content }}{% endif %}{% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}
<!-- event-history: vision.description -->
Observed {{ SubjectId }}: {{ Description }}{% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}
<!-- event-history: vision.relative_position -->
I observe {{ SubjectId }} {{ nf(Distance, 1) }} m {% if SubjectDirection == "Front" %}ahead of me{% elsif SubjectDirection == "Back" %}behind me{% elsif SubjectDirection == "Left" %}to my left{% else %}to my right{% endif %}; {% if ObserverDirection == "Front" %}they are facing me{% elsif ObserverDirection == "Back" %}their back is turned to me{% else %}I am to their {{ ObserverDirection | downcase }}{% endif %}.{% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}
<!-- event-history: fallback -->
((Received {{ TypeKey }} event.)){% if ObservedAt != blank %} (at {{ nf(ObservedAt, 1) }}s game time){% endif %}
