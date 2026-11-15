{{/*
Expand the name of the chart.
*/}}
{{- define "axonhub.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "axonhub.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{- define "axonhub.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "axonhub.labels" -}}
helm.sh/chart: {{ include "axonhub.chart" . }}
{{ include "axonhub.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{- define "axonhub.selectorLabels" -}}
app.kubernetes.io/name: {{ include "axonhub.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "axonhub.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "axonhub.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{- define "axonhub.postgresql.fullname" -}}
{{- printf "%s-postgresql" (include "axonhub.fullname" .) | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "axonhub.postgresql.serviceName" -}}
{{- include "axonhub.postgresql.fullname" . }}
{{- end }}

{{/*
True when the deployment must use AdoNet cluster membership + reminders.
Multi-replica and HPA deployments cannot use localhost clustering.
*/}}
{{- define "axonhub.clusterEnabled" -}}
{{- if or .Values.axonhub.cluster.enabled .Values.autoscaling.enabled (gt (int .Values.axonhub.replicaCount) 1) -}}
true
{{- else -}}
false
{{- end -}}
{{- end }}

{{/*
Npgsql key-value DSN for the application. A postgres:// URI is not accepted.
*/}}
{{- define "axonhub.databaseDsn" -}}
{{- if .Values.axonhub.database.dsn -}}
{{- .Values.axonhub.database.dsn -}}
{{- else if .Values.postgresql.enabled -}}
{{- if .Values.postgresql.auth.existingSecret -}}
{{- fail "when postgresql.auth.existingSecret is set, configure axonhub.database.existingSecret or axonhub.database.dsn" -}}
{{- end -}}
{{- printf "Host=%s;Port=%v;Database=%s;Username=%s;Password=%s" (include "axonhub.postgresql.serviceName" .) .Values.postgresql.service.port .Values.postgresql.auth.database .Values.postgresql.auth.username .Values.postgresql.auth.password -}}
{{- else -}}
{{- fail "axonhub.database.dsn or axonhub.database.existingSecret is required when postgresql.enabled=false" -}}
{{- end -}}
{{- end }}
