
-- Создание схемы
CREATE SCHEMA IF NOT EXISTS data;

-- Таблица user_id
CREATE TABLE IF NOT EXISTS data.user_id (
    id BIGSERIAL PRIMARY KEY,
    username TEXT NOT NULL UNIQUE,
    password TEXT NOT NULL,
    email TEXT NOT NULL UNIQUE
);

-- Таблица mes_his
CREATE TABLE IF NOT EXISTS data.mes_his (
    id SERIAL PRIMARY KEY,
    sender_name VARCHAR(100) NOT NULL,
    recipient_name VARCHAR(100) NOT NULL,
    message TEXT NOT NULL,
    message_type VARCHAR(50) NOT NULL,
    created_at TIMESTAMP DEFAULT NOW()
);

-- Таблица mes_arc
CREATE TABLE IF NOT EXISTS data.mes_arc (
    id SERIAL PRIMARY KEY,
    sender_name VARCHAR(100) NOT NULL,
    recipient_name VARCHAR(100) NOT NULL,
    message TEXT NOT NULL,
    message_type VARCHAR(50) NOT NULL,
    created_at TIMESTAMP DEFAULT NOW(),
    archived_at TIMESTAMP DEFAULT NOW()
);

-- Индексы для производительности
CREATE INDEX IF NOT EXISTS idx_user_id_username ON data.user_id(username);
CREATE INDEX IF NOT EXISTS idx_user_id_email ON data.user_id(email);
CREATE INDEX IF NOT EXISTS idx_mes_his_created ON data.mes_his(created_at);
CREATE INDEX IF NOT EXISTS idx_mes_his_sender ON data.mes_his(sender_name);
CREATE INDEX IF NOT EXISTS idx_mes_arc_created ON data.mes_arc(created_at);  -- Добавьте и для arc

-- Тестовые данные
INSERT INTO data.user_id (username, password, email) 
VALUES ('test', 'test123', 'test@example.com')
ON CONFLICT (username) DO NOTHING;

INSERT INTO data.mes_his (sender_name, recipient_name, message, message_type) 
VALUES ('test', 'user2', 'Test message 1', '1'),
       ('user2', 'test', 'Test message 2', '1')
ON CONFLICT DO NOTHING;  -- Добавьте ON CONFLICT для mes_his