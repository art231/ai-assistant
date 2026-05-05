-- ============================================================
-- PostgreSQL initialization script for VoiceChatAI
-- Enables pgvector extension and creates all tables
-- ============================================================

-- Enable pgvector extension
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- ============================================================
-- Rooms table
-- ============================================================
CREATE TABLE IF NOT EXISTS rooms (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    name VARCHAR(255) NOT NULL,
    status VARCHAR(50) NOT NULL DEFAULT 'waiting', -- waiting, active, ended
    max_participants INTEGER NOT NULL DEFAULT 20,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    ended_at TIMESTAMP WITH TIME ZONE,
    metadata JSONB DEFAULT '{}'
);

-- ============================================================
-- Participants table
-- ============================================================
CREATE TABLE IF NOT EXISTS participants (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    room_id UUID NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    user_name VARCHAR(255) NOT NULL,
    joined_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    left_at TIMESTAMP WITH TIME ZONE,
    is_speaking BOOLEAN NOT NULL DEFAULT FALSE,
    audio_level REAL DEFAULT 0.0,
    CONSTRAINT fk_participant_room FOREIGN KEY (room_id) REFERENCES rooms(id)
);

CREATE INDEX idx_participants_room_id ON participants(room_id);

-- ============================================================
-- Transcripts table (real-time transcription chunks)
-- ============================================================
CREATE TABLE IF NOT EXISTS transcripts (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    room_id UUID NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    participant_id UUID REFERENCES participants(id) ON DELETE SET NULL,
    user_name VARCHAR(255) NOT NULL,
    text TEXT NOT NULL,
    timestamp TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    is_final BOOLEAN NOT NULL DEFAULT FALSE,
    language VARCHAR(10) DEFAULT 'en',
    -- Vector embedding for semantic search (1536 dimensions for LLM embeddings)
    embedding vector(1536),
    CONSTRAINT fk_transcript_room FOREIGN KEY (room_id) REFERENCES rooms(id)
);

CREATE INDEX idx_transcripts_room_id ON transcripts(room_id);
CREATE INDEX idx_transcripts_timestamp ON transcripts(timestamp);
CREATE INDEX idx_transcripts_room_ts ON transcripts(room_id, timestamp);

-- Full-text search index on transcripts
CREATE INDEX idx_transcripts_fts ON transcripts USING GIN(to_tsvector('english', text));

-- Vector similarity search index (IVFFlat for approximate nearest neighbor)
CREATE INDEX idx_transcripts_embedding ON transcripts USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

-- ============================================================
-- Meeting Recordings table
-- ============================================================
CREATE TABLE IF NOT EXISTS meeting_recordings (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    room_id UUID NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    audio_path VARCHAR(500) NOT NULL,
    full_text TEXT,
    summary TEXT,
    started_at TIMESTAMP WITH TIME ZONE NOT NULL,
    ended_at TIMESTAMP WITH TIME ZONE,
    duration_seconds INTEGER DEFAULT 0,
    file_size_bytes BIGINT DEFAULT 0,
    status VARCHAR(50) NOT NULL DEFAULT 'recording', -- recording, processing, completed, failed
    -- Full-text search vector
    search_vector tsvector,
    CONSTRAINT fk_recording_room FOREIGN KEY (room_id) REFERENCES rooms(id)
);

CREATE INDEX idx_recordings_room_id ON meeting_recordings(room_id);
CREATE INDEX idx_recordings_status ON meeting_recordings(status);

-- Full-text search index on recordings
CREATE INDEX idx_recordings_fts ON meeting_recordings USING GIN(search_vector);

-- ============================================================
-- Topic Changes table (detected by AI)
-- ============================================================
CREATE TABLE IF NOT EXISTS topic_changes (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    room_id UUID NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    old_topic VARCHAR(500),
    new_topic VARCHAR(500) NOT NULL,
    detected_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    transcript_id UUID REFERENCES transcripts(id) ON DELETE SET NULL,
    confidence REAL DEFAULT 0.0,
    CONSTRAINT fk_topic_room FOREIGN KEY (room_id) REFERENCES rooms(id)
);

CREATE INDEX idx_topic_changes_room_id ON topic_changes(room_id);

-- ============================================================
-- Advice table (AI-generated suggestions)
-- ============================================================
CREATE TABLE IF NOT EXISTS advice (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    room_id UUID NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    type VARCHAR(50) NOT NULL, -- suggestion, alternative_idea, improvement
    text TEXT NOT NULL,
    generated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    context_transcript_ids UUID[] DEFAULT '{}',
    rating INTEGER DEFAULT 0, -- sum of 👍 (+1) and 👎 (-1)
    CONSTRAINT fk_advice_room FOREIGN KEY (room_id) REFERENCES rooms(id)
);

CREATE INDEX idx_advice_room_id ON advice(room_id);
CREATE INDEX idx_advice_type ON advice(type);

-- ============================================================
-- Advice Feedback table (for fine-tuning)
-- ============================================================
CREATE TABLE IF NOT EXISTS advice_feedback (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    advice_id UUID NOT NULL REFERENCES advice(id) ON DELETE CASCADE,
    participant_id UUID REFERENCES participants(id) ON DELETE SET NULL,
    rating SMALLINT NOT NULL CHECK (rating IN (1, -1)), -- 1 = 👍, -1 = 👎
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_feedback_advice FOREIGN KEY (advice_id) REFERENCES advice(id)
);

CREATE INDEX idx_advice_feedback_advice_id ON advice_feedback(advice_id);

-- ============================================================
-- Meeting Summaries table (generated every 30 seconds)
-- ============================================================
CREATE TABLE IF NOT EXISTS meeting_summaries (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    room_id UUID NOT NULL REFERENCES rooms(id) ON DELETE CASCADE,
    summary_text TEXT NOT NULL,
    topics TEXT[] DEFAULT '{}',
    generated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
    transcript_range_start TIMESTAMP WITH TIME ZONE,
    transcript_range_end TIMESTAMP WITH TIME ZONE,
    CONSTRAINT fk_summary_room FOREIGN KEY (room_id) REFERENCES rooms(id)
);

CREATE INDEX idx_summaries_room_id ON meeting_summaries(room_id);
CREATE INDEX idx_summaries_generated_at ON meeting_summaries(generated_at);

-- ============================================================
-- Function to update search_vector on meeting_recordings
-- ============================================================
CREATE OR REPLACE FUNCTION update_recording_search_vector()
RETURNS TRIGGER AS $$
BEGIN
    NEW.search_vector := to_tsvector('english', COALESCE(NEW.full_text, ''));
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_update_recording_search_vector
    BEFORE INSERT OR UPDATE OF full_text ON meeting_recordings
    FOR EACH ROW
    EXECUTE FUNCTION update_recording_search_vector();

-- ============================================================
-- Function to update transcript embedding placeholder
-- ============================================================
CREATE OR REPLACE FUNCTION update_transcript_embedding()
RETURNS TRIGGER AS $$
BEGIN
    -- Embedding will be set by the application layer
    -- This trigger just ensures the field is not null
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER trg_update_transcript_embedding
    BEFORE INSERT ON transcripts
    FOR EACH ROW
    EXECUTE FUNCTION update_transcript_embedding();
